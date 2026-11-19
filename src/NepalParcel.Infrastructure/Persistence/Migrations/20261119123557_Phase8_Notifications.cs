using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepalParcel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8_Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Audience",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Receiver");

            migrationBuilder.AddColumn<string>(
                name: "DedupeKey",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveredAtUtc",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryError",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryStatus",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FailedAtUtc",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderMessageId",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadAtUtc",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenderedBody",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAtUtc",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkipReason",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubmittedAtUtc",
                schema: "parcel",
                table: "NotificationOutbox",
                type: "datetimeoffset",
                nullable: true);

            // Before this phase nothing sent messages, so any "Sent" row is one a provider accepted: it is now "Submitted".
            // The one-time codes queued so far went to the receiver, which is the default audience above.
            migrationBuilder.Sql(
                "UPDATE [parcel].[NotificationOutbox] SET [Status] = 'Submitted', [SubmittedAtUtc] = [ProcessedAtUtc] WHERE [Status] = 'Sent'");

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                schema: "parcel",
                table: "NotificationLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OccurredAtUtc",
                schema: "parcel",
                table: "NotificationLogs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MockProviderMessages",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    To = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MockProviderMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationOptOuts",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOptOuts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTemplates",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProviderTemplateName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_CreatedAtUtc",
                schema: "parcel",
                table: "NotificationOutbox",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_DedupeKey_Audience_Channel",
                schema: "parcel",
                table: "NotificationOutbox",
                columns: new[] { "DedupeKey", "Audience", "Channel" },
                unique: true,
                filter: "[DedupeKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Provider_ProviderMessageId",
                schema: "parcel",
                table: "NotificationOutbox",
                columns: new[] { "Provider", "ProviderMessageId" },
                unique: true,
                filter: "[ProviderMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MockProviderMessages_CreatedAtUtc",
                schema: "parcel",
                table: "MockProviderMessages",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MockProviderMessages_ProviderMessageId",
                schema: "parcel",
                table: "MockProviderMessages",
                column: "ProviderMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOptOuts_CreatedAtUtc",
                schema: "parcel",
                table: "NotificationOptOuts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOptOuts_PhoneNumber_Channel",
                schema: "parcel",
                table: "NotificationOptOuts",
                columns: new[] { "PhoneNumber", "Channel" },
                unique: true,
                filter: "[RemovedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_Key_Channel_Language",
                schema: "parcel",
                table: "NotificationTemplates",
                columns: new[] { "Key", "Channel", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The earlier model has no "Submitted", "Skipped" or "Cancelled" status.
            migrationBuilder.Sql("UPDATE [parcel].[NotificationOutbox] SET [Status] = 'Sent' WHERE [Status] = 'Submitted'");
            migrationBuilder.Sql("UPDATE [parcel].[NotificationOutbox] SET [Status] = 'DeadLetter' WHERE [Status] IN ('Skipped', 'Cancelled')");

            migrationBuilder.DropTable(
                name: "MockProviderMessages",
                schema: "parcel");

            migrationBuilder.DropTable(
                name: "NotificationOptOuts",
                schema: "parcel");

            migrationBuilder.DropTable(
                name: "NotificationTemplates",
                schema: "parcel");

            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_CreatedAtUtc",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_DedupeKey_Audience_Channel",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_Provider_ProviderMessageId",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "Audience",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "DedupeKey",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "DeliveredAtUtc",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "DeliveryError",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "FailedAtUtc",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "Language",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "ProviderMessageId",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "ReadAtUtc",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "RenderedBody",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "SentAtUtc",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "SkipReason",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "SubmittedAtUtc",
                schema: "parcel",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                schema: "parcel",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "OccurredAtUtc",
                schema: "parcel",
                table: "NotificationLogs");
        }
    }
}
