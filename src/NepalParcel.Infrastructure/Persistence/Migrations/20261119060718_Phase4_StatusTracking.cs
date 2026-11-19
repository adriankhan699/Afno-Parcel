using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepalParcel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4_StatusTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.CreateTable(
                name: "TrackingOtps",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParcelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingOtps", x => x.Id);
                    table.CheckConstraint("CK_TrackingOtps_Attempts", "[FailedAttempts] >= 0");
                    table.ForeignKey(
                        name: "FK_TrackingOtps_Parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalSchema: "parcel",
                        principalTable: "Parcels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Code", "Name" },
                values: new object[] { "BookingCreated", "Booking created" });

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 2,
                column: "Description",
                value: "Collected from the sender or received at the counter.");

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "Code", "Description", "Name" },
                values: new object[] { "Shipped", "Dispatched from the origin branch into the network.", "Shipped" });

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "Code", "Description", "Name" },
                values: new object[] { "ArrivedAtWarehouse", "Received and scanned at a warehouse or sorting hub.", "Arrived at warehouse" });

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 7,
                column: "Description",
                value: "Handed over to the receiver.");

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 8,
                column: "Description",
                value: "Receiver unavailable or address problem.");

            migrationBuilder.CreateIndex(
                name: "IX_TrackingOtps_ParcelId_CreatedAtUtc",
                schema: "parcel",
                table: "TrackingOtps",
                columns: new[] { "ParcelId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackingOtps",
                schema: "parcel");

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Code", "Name" },
                values: new object[] { "Booked", "Booked" });

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 2,
                column: "Description",
                value: "Collected from the sender.");

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "Code", "Description", "Name" },
                values: new object[] { "ReceivedAtBranch", "Received and checked at an Afno Parcel branch.", "Received at branch" });

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "Code", "Description", "Name" },
                values: new object[] { "ReceivedAtWarehouse", "Stored at a warehouse or sorting hub.", "Received at warehouse" });

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 7,
                column: "Description",
                value: "Handed over to the recipient.");

            migrationBuilder.UpdateData(
                schema: "parcel",
                table: "ParcelStatuses",
                keyColumn: "Id",
                keyValue: 8,
                column: "Description",
                value: "Recipient unavailable or address problem.");

            migrationBuilder.InsertData(
                schema: "parcel",
                table: "ParcelStatuses",
                columns: new[] { "Id", "Code", "Description", "IsPublic", "IsTerminal", "Name", "SortOrder" },
                values: new object[] { 9, "ReturnToSender", "Being sent back to the sender.", true, false, "Return to sender", 9 });
        }
    }
}
