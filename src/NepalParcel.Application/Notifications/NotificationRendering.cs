using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Application.Notifications;

/// <summary>Which parcel status changes send a message, and which template each one uses.</summary>
public static class NotificationEvents
{
    /// <summary>The template for a status change, or null when that change sends nothing (a cancelled booking, for example).</summary>
    public static string? TemplateFor(ParcelStatusCode status) => status switch
    {
        ParcelStatusCode.BookingCreated => NotificationTemplateKeys.ParcelBooked,
        ParcelStatusCode.PickedUp => NotificationTemplateKeys.ParcelPickedUp,
        ParcelStatusCode.Shipped => NotificationTemplateKeys.ParcelShipped,
        ParcelStatusCode.InTransit => NotificationTemplateKeys.ParcelInTransit,
        ParcelStatusCode.ArrivedAtWarehouse => NotificationTemplateKeys.ParcelAtWarehouse,
        ParcelStatusCode.OutForDelivery => NotificationTemplateKeys.ParcelOutForDelivery,
        ParcelStatusCode.Delivered => NotificationTemplateKeys.ParcelDelivered,
        ParcelStatusCode.DeliveryFailed => NotificationTemplateKeys.ParcelDeliveryFailed,
        ParcelStatusCode.Returned => NotificationTemplateKeys.ParcelReturned,
        _ => null,
    };
}

/// <summary>
/// The placeholders each message may use. Names and phone numbers are deliberately not available: a message goes through a
/// provider and sits on a phone, so it carries a tracking number and a link, and the link leads to the real details.
/// A failed delivery's reason is for staff and never appears in a message.
/// </summary>
public static class TemplatePlaceholders
{
    private static readonly string[] ParcelEvent = ["tracking", "link", "service", "eta"];
    private static readonly string[] Code = ["code", "minutes", "tracking"];

    public static IReadOnlyList<string> Allowed(string key) => NotificationTemplateKeys.IsOneTimeCode(key) ? Code : ParcelEvent;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedByKey { get; } =
        NotificationTemplateKeys.All.ToDictionary(k => k, Allowed, StringComparer.Ordinal);

    /// <summary>Values used by the preview so a template can be checked before it is saved.</summary>
    public static IReadOnlyDictionary<string, string> Samples { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tracking"] = "NP-20260101-ABC123",
        ["link"] = "https://example.com/track/NP-20260101-ABC123",
        ["service"] = "Standard",
        ["eta"] = "05 Jan 2026",
        ["code"] = "123456",
        ["minutes"] = "10",
    };
}

/// <summary>Fills <c>{name}</c> placeholders and finds what is wrong with a template.</summary>
public static partial class TemplateRenderer
{
    public const int MaxSmsLength = 480;
    public const int MaxWhatsAppParameters = 10;

    /// <summary>The placeholder names in the order they first appear, each once. This is the order WhatsApp's numbered fields take.</summary>
    public static IReadOnlyList<string> Parameters(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var seen = new List<string>();
        foreach (Match match in PlaceholderRegex().Matches(body))
        {
            var name = match.Groups[1].Value;
            if (!seen.Contains(name, StringComparer.Ordinal))
            {
                seen.Add(name);
            }
        }

        return seen;
    }

    /// <summary>A placeholder with no value becomes empty text.</summary>
    public static string Render(string body, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(values);
        return PlaceholderRegex().Replace(body, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : string.Empty);
    }

    /// <summary>The values for the placeholders, in the order <see cref="Parameters"/> lists them.</summary>
    public static IReadOnlyList<string> ParameterValues(string body, IReadOnlyDictionary<string, string> values) =>
        [.. Parameters(body).Select(name => values.TryGetValue(name, out var value) ? value : string.Empty)];

    /// <summary>What is wrong with the text, in plain words. Empty when it can be saved.</summary>
    public static IReadOnlyList<string> Validate(string key, NotificationChannel channel, string? body)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(body))
        {
            problems.Add("The message text is empty.");
            return problems;
        }

        if (body.Length > NotificationTemplate.MaxBodyLength)
        {
            problems.Add($"The text is longer than {NotificationTemplate.MaxBodyLength} characters.");
        }

        var allowed = TemplatePlaceholders.Allowed(key);
        var names = Parameters(body);
        foreach (var name in names.Where(n => !allowed.Contains(n, StringComparer.Ordinal)))
        {
            problems.Add($"'{{{name}}}' is not available in this message. Use: {string.Join(", ", allowed.Select(a => "{" + a + "}"))}.");
        }

        var leftover = PlaceholderRegex().Replace(body, string.Empty);
        if (leftover.Contains('{', StringComparison.Ordinal) || leftover.Contains('}', StringComparison.Ordinal))
        {
            problems.Add("A curly bracket is not part of a placeholder such as {tracking}.");
        }

        if (channel == NotificationChannel.Sms && body.Length > MaxSmsLength)
        {
            problems.Add($"An SMS longer than {MaxSmsLength} characters is split into many parts and costs more. Shorten it.");
        }

        if (channel == NotificationChannel.WhatsApp && names.Count > MaxWhatsAppParameters)
        {
            problems.Add($"A WhatsApp template can have at most {MaxWhatsAppParameters} fields.");
        }

        return problems;
    }

    /// <summary>
    /// Turns the JSON stored on an outbox row into placeholder values. One-time-code rows were queued by earlier code with the
    /// names <c>trackingNumber</c> and <c>validForMinutes</c>; they are given the names templates use.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ValuesFromPayload(string payloadJson)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return values;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var text = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };

                if (text is not null)
                {
                    values[property.Name] = text;
                }
            }
        }
        catch (JsonException)
        {
            return values;
        }

        Alias(values, "trackingNumber", "tracking");
        Alias(values, "validForMinutes", "minutes");
        return values;
    }

    /// <summary>The text a person is shown: dates in Nepal time.</summary>
    public static string FormatDate(DateTimeOffset? utc) =>
        utc is { } value
            ? NepalParcel.Domain.Common.NepalTime.ToLocal(value).ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : string.Empty;

    private static void Alias(Dictionary<string, string> values, string from, string to)
    {
        if (values.TryGetValue(from, out var value) && !values.ContainsKey(to))
        {
            values[to] = value;
        }
    }

    [GeneratedRegex(@"\{([a-z][a-zA-Z0-9]*)\}")]
    private static partial Regex PlaceholderRegex();
}

/// <summary>
/// The texts the system starts with. They are written to the database once, when it is first set up, and can then be edited by
/// the administrator; editing never touches them here.
/// </summary>
public static class DefaultTemplates
{
    public sealed record Seed(string Key, NotificationChannel Channel, string Body, string? ProviderTemplateName);

    private static readonly (string Key, string Body)[] ParcelTexts =
    [
        (NotificationTemplateKeys.ParcelBooked, "Afno Parcel: your parcel {tracking} has been booked. Track it: {link}"),
        (NotificationTemplateKeys.ParcelPickedUp, "Afno Parcel: parcel {tracking} has been picked up. Track it: {link}"),
        (NotificationTemplateKeys.ParcelShipped, "Afno Parcel: parcel {tracking} has been shipped. Track it: {link}"),
        (NotificationTemplateKeys.ParcelInTransit, "Afno Parcel: parcel {tracking} is on its way. Track it: {link}"),
        (NotificationTemplateKeys.ParcelAtWarehouse, "Afno Parcel: parcel {tracking} has arrived at one of our warehouses. Track it: {link}"),
        (NotificationTemplateKeys.ParcelOutForDelivery, "Afno Parcel: parcel {tracking} is out for delivery today. Track it: {link}"),
        (NotificationTemplateKeys.ParcelDelivered, "Afno Parcel: parcel {tracking} has been delivered. Thank you for using Afno Parcel."),
        (NotificationTemplateKeys.ParcelDeliveryFailed, "Afno Parcel: we could not deliver parcel {tracking}. We will contact you about another attempt. Track it: {link}"),
        (NotificationTemplateKeys.ParcelReturned, "Afno Parcel: parcel {tracking} has been returned. Track it: {link}"),
    ];

    private static readonly (string Key, string Body)[] CodeTexts =
    [
        (NotificationTemplateKeys.TrackingOtp, "Afno Parcel: your tracking code is {code}. It works for {minutes} minutes. Do not share it."),
        (NotificationTemplateKeys.DeliveryOtp, "Afno Parcel: give code {code} to the delivery agent to receive parcel {tracking}. It works for {minutes} minutes. Do not share it with anyone else."),
    ];

    /// <summary>SMS for every message; WhatsApp for the parcel events (one-time codes go by SMS only).</summary>
    public static IReadOnlyList<Seed> All { get; } =
    [
        .. ParcelTexts.Select(t => new Seed(t.Key, NotificationChannel.Sms, t.Body, null)),
        .. CodeTexts.Select(t => new Seed(t.Key, NotificationChannel.Sms, t.Body, null)),
        .. ParcelTexts.Select(t => new Seed(t.Key, NotificationChannel.WhatsApp, t.Body, WhatsAppTemplateName(t.Key))),
    ];

    /// <summary>The suggested name to submit to Meta: <c>parcel-out-for-delivery</c> → <c>afno_parcel_out_for_delivery</c>.</summary>
    public static string WhatsAppTemplateName(string key)
    {
        var builder = new StringBuilder("afno_");
        foreach (var c in key)
        {
            builder.Append(c == '-' ? '_' : c);
        }

        return builder.ToString();
    }
}
