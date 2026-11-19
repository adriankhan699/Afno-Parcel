# Afno Parcel — WhatsApp and SMS notifications (Phase 8)

How customers hear about their parcels: which events send a message, who is messaged and who is not, how a message is queued,
sent, retried and reported on, and how to connect the real providers. **Out of the box nothing leaves the application:** both
channels use *mock providers* that keep messages in a test inbox. Connecting WhatsApp (Meta) is a configuration change plus the
steps in [Connecting the real providers](#connecting-the-real-providers).

> **Read this first.** The Meta WhatsApp client was written from Meta's published request and response shapes **without access
> to a Meta account**, and it has been run only against a fake HTTP server. No Nepal SMS gateway is implemented (their
> interfaces were not available): the SMS side is an abstraction plus the mock. Both are covered by tests of *our* behaviour; try
> WhatsApp with a Meta test number before relying on it.

## The events

| Parcel status | Message (template key) |
|---------------|------------------------|
| Booking created | `parcel-booked` |
| Picked up | `parcel-picked-up` |
| Shipped | `parcel-shipped` |
| In transit | `parcel-in-transit` |
| Arrived at warehouse | `parcel-at-warehouse` |
| Out for delivery | `parcel-out-for-delivery` |
| Delivered | `parcel-delivered` |
| Delivery failed | `parcel-delivery-failed` |
| Returned | `parcel-returned` |

A cancelled booking sends nothing. Two one-time-code messages (`tracking-otp`, `delivery-otp`, SMS only) use the same pipeline.
Every status change goes through one place in the code (the status changer), so the status API, warehouse scans and delivery
agents all notify the same way.

## Who is messaged

| Person | Rule |
|--------|------|
| **Sender** (the customer) | Only if they **agreed** for that channel. Silence is not agreement: both channels are off until the customer turns them on (tick-boxes when booking, the *Parcel updates* page, or counter staff recording what was said at the desk). Withdrawing is just as easy and takes effect on messages not yet sent. |
| **Receiver** | Not a customer, so there is nobody to ask: messaged on parcel events unless their number has **opted out**, or the operator switched receiver messages off (`Notifications:Sms:NotifyReceivers`, `Notifications:WhatsApp:NotifyReceivers`). *WhatsApp's own rules expect the person to have opted in; decide this for your business and switch receiver WhatsApp off if you cannot show that.* |
| A person who is sender and receiver | One message per channel, as the sender. |
| A landline | Never (SMS and WhatsApp need a mobile). |
| One-time codes | To the receiver's phone by SMS, because they asked for it; no consent check and no opt-out check. |

**Opt-outs** are per phone number and per channel, apply to everybody with that number, and **always win over consent**. They come
from a customer replying `STOP` on WhatsApp (and `START` to lift it) or from staff (`/notifications/opt-outs`). Lifting an
opt-out keeps the row, so the history stays. *Replying STOP to an SMS is not handled: SMS gateways each deliver replies in their
own way, so for SMS staff record the opt-out.*

Consent is stored as the existing `CustomerConsents` history (new type `WhatsAppNotifications`): each change adds a row with the
time, source (web or counter), policy version and IP address; the latest row per type is the current choice. Nothing is edited
or deleted.

Every decision is recorded. A message that will not be sent is still written to the outbox as **Skipped** with the reason ("The
sender has not agreed to receive WhatsApp messages.", "The number has opted out of SMS messages.", "There is no active SMS
template for this message.") so staff can answer "why didn't they get a message?".

## The pipeline

```
status change ──(same transaction)──▶ outbox rows ──▶ Hangfire worker ──▶ provider ──▶ phone
                                           ▲                                  │
                                           └──── webhook (delivery reports) ◀─┘
```

1. **Transactional outbox.** The status change and its outbox rows are saved together or not at all: a rolled-back change sends
   nothing, and a saved change cannot lose its message. A unique index on *(status-change id, audience, channel)* makes it
   impossible to queue the same event twice.
2. **Background worker.** A Hangfire recurring job (every minute) sweeps the outbox; a hint also asks it to run right after a save
   that queued something, so messages normally go out within a moment. The hint is optional: if it is lost, the sweep finds the
   row. A worker **claims** a message first (an optimistic-concurrency row version stops two workers taking one), and a message
   left locked by a worker that died becomes due again after `Notifications:Outbox:LockSeconds`.
3. **At send time** consent and opt-outs are checked again (they may have changed since queueing); the template is filled in; the
   provider is called.
4. **Retry with exponential backoff.** A temporary failure waits `BaseDelaySeconds` (30), then double, then double again, up to
   `MaxDelaySeconds` (3600), with ±20 % jitter so retries do not arrive together. After `MaxAttempts` (5) the message is a
   **dead letter**. A permanent refusal (invalid number, missing template) is dead-lettered at once. A super administrator can
   **requeue** a dead letter (a fresh set of attempts) or **cancel** a waiting one.
5. **Every attempt is recorded** in `NotificationLogs` (attempt number, provider, the provider's message id, masked number, error)
   and so is every report the provider sends back, so a message's detail shows one history.

### Submitted, sent, delivered, failed — kept apart

| | Meaning | Set by |
|---|---|---|
| `Status` | What **we** did: `Pending`, `Processing`, `Submitted` (the provider accepted it and gave an id), `Failed` (will retry), `DeadLetter` (gave up), `Skipped`, `Cancelled` | our worker |
| `DeliveryStatus` | What the **provider reported**: `None`, `Sent`, `Delivered`, `Read`, `Failed` | **only** the provider's webhook |

A provider accepting a request makes a message `Submitted` with a delivery status of `None` — **never** `Delivered`. The delivery
status moves forward only (sent → delivered → read); a late, repeated or out-of-order report changes nothing, and a failure
report never undoes a delivery. Timestamps for submitted, sent, delivered, read and failed are kept separately, and the summary
counts them separately.

The text of one-time-code messages is never stored in the outbox: after sending (or giving up) the code is erased from the
payload and the rendered text is not kept.

## Webhooks

`GET /api/v1/webhooks/whatsapp` answers Meta's endpoint check (echoes the challenge when the verify token matches).
`POST /api/v1/webhooks/whatsapp` receives delivery reports and customer replies.

- **Signature first.** Meta signs each call (`X-Hub-Signature-256`, HMAC-SHA256 of the exact bytes with the app secret). The
  signature is compared in constant time **before anything is read**; a missing or wrong one is `401` and nothing is stored. With
  no app secret configured nothing can be verified, so nothing is accepted.
- **Idempotent.** Each report is stored once in `WebhookEvents` under a unique *(provider, event id)* (for statuses the event id is
  the message id and the status). A repeat is acknowledged with `200` and changes nothing.
- **Early reports are kept.** A report can arrive before our save of the provider's message id has landed. It stays `Received` and
  the sweep tries it again for `WebhookRetryHours` (24).
- Stored reports contain the message id, the status and the time — **no phone number** and nothing the customer wrote, except that
  a reply is acted on only if it is exactly `STOP` / `START` (and a few synonyms).
- Two identical calls arriving at the same instant can make the second fail on the unique index; that call gets a `500`, the
  provider retries, and the retry is recognised as a repeat.

## Settings

Nothing secret is in the committed files.

| Setting | Meaning |
|---------|---------|
| `Notifications:Enabled` | Master switch for parcel messages (default `true`). |
| `Notifications:WhatsApp:Provider` | `Mock` (default) or `Meta`. |
| `Notifications:WhatsApp:NotifyReceivers`, `Notifications:Sms:NotifyReceivers` | Message receivers on this channel (default `true`). |
| `Notifications:WhatsApp:PhoneNumberId` | Meta's id of the sending number. |
| `Notifications:WhatsApp:AccessToken` | **Secret.** A system user's token. |
| `Notifications:WhatsApp:AppSecret` | **Secret.** Verifies webhook signatures. |
| `Notifications:WhatsApp:VerifyToken` | **Secret.** Typed into Meta's webhook setup; compared when Meta checks the endpoint. |
| `Notifications:WhatsApp:ApiVersion`, `BaseUrl`, `TimeoutSeconds` | `v21.0`, `https://graph.facebook.com`, 15. |
| `Notifications:Sms:Provider` | `Mock` (default), or the name of a gateway you add. `SenderId` is optional. |
| `Notifications:Outbox:*` | `BatchSize` 25, `LockSeconds` 180, `MaxAttempts` 5, `BaseDelaySeconds` 30, `MaxDelaySeconds` 3600, `WebhookRetryHours` 24. |
| `Hangfire:Mode` | `SqlServer` (production default: tables in schema `hangfire` of the application database; the connection needs rights to create it), `InMemory` (Development default: lost on restart, which is safe because the outbox is in the database), or `Disabled` (no worker: nothing is sent; used by the tests). |
| `Hangfire:WorkerCount`, `SweepCron` | 2 workers, every minute. |
| `Hangfire:DashboardEnabled` | Off by default. When on, `/hangfire` is **read-only and answers only requests from the same machine.** Never expose it publicly. |
| `Database:SeedNotificationTemplates` | `true`: adds default message texts that are missing (never overwrites edits). |
| `RateLimiting:WebhookPermitPerMinute` | 600 per client IP for the webhook endpoints. |

With `Provider = Meta` the application **refuses to start** unless the phone number id, access token, app secret and verify token
are all set. At startup it logs a warning for every channel still on a mock provider, and for a provider missing credentials.
`GET /api/v1/notifications/providers` shows which provider is in use (never credentials).

## Connecting the real providers

### WhatsApp (Meta Cloud API)

1. In Meta for Developers create an app with the **WhatsApp** product and a WhatsApp Business Account. Add a phone number (for
   testing, use the test number Meta gives you and add your own phone to its allowed recipients).
2. Create a **system user** with the `whatsapp_business_messaging` permission and generate a token. Note the **phone number id**
   and the app's **app secret**. Choose any long random **verify token**.
3. Store the secrets (never in a file in the repository):

   ```powershell
   cd src\NepalParcel.API
   dotnet user-secrets set "Notifications:WhatsApp:Provider" "Meta"
   dotnet user-secrets set "Notifications:WhatsApp:PhoneNumberId" "<phone number id>"
   dotnet user-secrets set "Notifications:WhatsApp:AccessToken" "<system user token>"
   dotnet user-secrets set "Notifications:WhatsApp:AppSecret" "<app secret>"
   dotnet user-secrets set "Notifications:WhatsApp:VerifyToken" "<your verify token>"
   ```

   In production use environment variables (`Notifications__WhatsApp__AccessToken`, …) or your secret store.
4. Make the API reachable over **HTTPS** from the internet (for testing, a tunnel to your machine works). In the app's WhatsApp
   configuration set the webhook callback to `https://<host>/api/v1/webhooks/whatsapp` with your verify token, and subscribe to
   the **messages** field.
5. **Submit the message templates** below to Meta for approval (WhatsApp lets a business start a conversation only with an
   approved template). Use category *Utility*, language *English (en)*. The fields `{{1}}`, `{{2}}` must be in the order shown.
   If you change a name or the text in Meta, edit the template on the *Notifications → Templates* screen (the *WhatsApp template
   name* must be exactly the approved name; for a WhatsApp message the text there is only our copy).
6. Restart the API, open *Notifications → Overview*: the provider card should say **Meta WhatsApp** and *not* mock. Book a parcel to
   your own number with WhatsApp updates ticked, and watch the message go `Submitted`, then `Sent` / `Delivered` as Meta reports.

Suggested templates (the placeholders in our text become Meta's numbered fields in order of first appearance):

| Template name | Body |
|---------------|------|
| `afno_parcel_booked` | Afno Parcel: your parcel {{1}} has been booked. Track it: {{2}} |
| `afno_parcel_picked_up` | Afno Parcel: parcel {{1}} has been picked up. Track it: {{2}} |
| `afno_parcel_shipped` | Afno Parcel: parcel {{1}} has been shipped. Track it: {{2}} |
| `afno_parcel_in_transit` | Afno Parcel: parcel {{1}} is on its way. Track it: {{2}} |
| `afno_parcel_at_warehouse` | Afno Parcel: parcel {{1}} has arrived at one of our warehouses. Track it: {{2}} |
| `afno_parcel_out_for_delivery` | Afno Parcel: parcel {{1}} is out for delivery today. Track it: {{2}} |
| `afno_parcel_delivered` | Afno Parcel: parcel {{1}} has been delivered. Thank you for using Afno Parcel. |
| `afno_parcel_delivery_failed` | Afno Parcel: we could not deliver parcel {{1}}. We will contact you about another attempt. Track it: {{2}} |
| `afno_parcel_returned` | Afno Parcel: parcel {{1}} has been returned. Track it: {{2}} |

Meta's error codes were classified from memory of their documentation: throttling codes are retried, a rejected template or
number is not, an invalid or expired token is retried (so messages recover once the token is fixed). Review the lists in
`MetaWhatsAppCloudProvider` against Meta's current error reference.

### SMS (a Nepal gateway)

The SMS provider is an interface, so a gateway is added without touching the rest. Nothing here was written against a real
gateway: look up your gateway's API (Sparrow SMS, Aakash SMS and others each differ) and implement it yourself.

1. Add a class that implements `ISmsProvider` (`src/NepalParcel.Application/Notifications`):
   - `Name` is stored on messages (for example `SparrowSms`); `ConfigurationName` is what goes in settings.
   - `SendAsync` receives the number with the country code (`9779841234567`) and the finished text, and returns
     `Accepted(messageId)`, `RetryLater(error)` or `Rejected(error)`. It must not throw for gateway problems.
   - Read the gateway's token from configuration at the moment of the call; never log it.
2. Register it next to the others (`services.AddScoped<IMessageProvider, SparrowSmsProvider>()`), set
   `Notifications:Sms:Provider` to its `ConfigurationName`, and store its credentials with user-secrets.
3. If the gateway sends delivery reports, add a webhook endpoint that checks the gateway's authentication, then calls
   `INotificationWebhookService` in the same way the mock does — the code that applies a report (`NotificationWebhookService`)
   is shared and already idempotent and forward-only.

A gateway name that is not registered is reported plainly in `/notifications/providers` and the message fails with that reason; it is
never silently replaced by the mock.

## The mock providers

`Mock` is the default for both channels. **Nothing is sent to any phone.** A message is written to the *test inbox* (SuperAdmin →
*Notifications → Test inbox*, or `GET /api/v1/notifications/mock/messages`) with the text, or for WhatsApp the template name and its
fields, and the provider gives it an id as a real one would. It then stays `Submitted` until you make the mock report: press
*Report sent / delivered / read / failed* on the inbox (`POST /api/v1/notifications/mock/messages/{providerMessageId}/status`).
That goes through the **same webhook code** a real report does, so duplicates, out-of-order reports and "delivered" only after a
report all behave as in production. Both endpoints answer `404` when no mock provider is in use.

## Message texts

Defaults are written the first time the system runs (SMS for all eleven messages, WhatsApp for the nine parcel events). An
administrator edits them on *Notifications → Templates*. Placeholders are `{tracking}`, `{link}`, `{service}`, `{eta}` (and for codes
`{code}`, `{minutes}`). **Names, phone numbers and the reason a delivery failed cannot be put in a message**: a message passes through a
provider and sits on a phone, so it carries a tracking number and a link, and the link leads to what the tracking page may show.
The preview fills in sample values and lists problems; saving refuses unknown placeholders, stray brackets, an SMS of more than 480
characters, and an active WhatsApp message with no approved template name.

## API

| Method | Route | Who |
|--------|-------|-----|
| GET | `/api/v1/notifications/messages`, `…/messages/{id}`, `…/summary`, `…/providers`, `…/templates`, `…/opt-outs` | `SuperAdmin`, `BranchManager` |
| POST | `…/messages/{id}/requeue`, `…/messages/{id}/cancel` | `SuperAdmin` |
| PUT | `…/templates/{id}` | `SuperAdmin` |
| POST | `…/templates/preview` | `SuperAdmin`, `BranchManager` |
| POST, DELETE | `…/opt-outs`, `…/opt-outs/{id}` | `SuperAdmin` |
| GET | `/api/v1/parcels/{id}/notifications` | staff who read parcels |
| GET, PUT | `…/notifications/preferences` | `Customer` (their own choices) |
| GET, PUT | `…/notifications/consent/parcel/{parcelId}` | counter staff (the sender's choices) |
| GET, POST | `…/notifications/mock/messages`, `…/mock/messages/{providerMessageId}/status` | `SuperAdmin`, mock only |
| GET, POST | `/api/v1/webhooks/whatsapp` | anonymous, **signature required**, rate limited |
| POST | `/api/v1/webhooks/mock` | `SuperAdmin`, mock only |

`POST /api/v1/parcels` accepts `notifyBySms` and `notifyByWhatsApp` (booleans, optional). They are recorded as consent **before** the
booking message is planned, so the first message follows the choice; leaving them out keeps what is already recorded.
Phone numbers are always shown masked (`98******67`). Errors are the usual problem responses.

## Data model

New tables `NotificationTemplates`, `NotificationOptOuts` (unique per number and channel while in force) and `MockProviderMessages`.
`NotificationOutbox` gains the audience, language, dedupe key, provider and provider message id, rendered text, skip reason,
delivery status and the sent / delivered / read / failed times (unique indexes on the dedupe key and on the provider's message id);
`NotificationLogs` gains the attempt number and the provider's time. Migration `Phase8_Notifications` gives existing rows sensible
values (`Sent` becomes `Submitted`). See [database.md](database.md).

## Known limits

- **At-least-once.** If the process dies after a provider accepted a message but before we saved that, the message is sent again
  when its lock expires. WhatsApp has no way to prevent that; a rare duplicate message is accepted over a lost one.
- The worker's claim relies on SQL Server's row version. The integration tests run on SQLite, which has none, so the *race itself*
  is not exercised there (the stale-lock recovery and the rest are).
- Only English texts exist; the `Language` column is ready for Nepali.
- SMS replies (STOP) and SMS delivery reports need a gateway-specific webhook that you add with the gateway.
- Not run against Meta (see the note at the top).

## Tests

- **Unit:** the outbox state machine and the forward-only delivery status (including late, repeated and out-of-order reports and a
  failure never undoing a delivery); consent and opt-out rules as a truth table; the planner (each event, consent, opt-outs,
  receivers, landlines, dedupe key, nothing for a cancelled booking); template rendering and validation (every default template is
  valid); the backoff (doubling, cap, jitter); webhook signatures (valid, missing, malformed, tampered, no secret), the endpoint
  check, duplicates, early reports, STOP and START; the provider registry and settings validation.
- **Integration** (the real API, in-memory SQLite, a scripted provider): the whole journey sending a message for every event to both
  people on both channels; *accepted is not delivered*; the attempt log; the test inbox; reports through the webhook path; retry
  with a growing delay, dead letters, requeue and cancel; a provider that throws; a worker that died; consent withdrawn and opt-outs
  added after queueing; one-time codes and their erasure; the Meta client against a fake HTTP server (what is sent and how every
  kind of answer is read); the authorization matrix; templates; preferences; and the signed webhook endpoint.
