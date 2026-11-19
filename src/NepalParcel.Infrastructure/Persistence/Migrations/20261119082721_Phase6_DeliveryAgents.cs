using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepalParcel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6_DeliveryAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeliveryOtps",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParcelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryOtps", x => x.Id);
                    table.CheckConstraint("CK_DeliveryOtps_Attempts", "[FailedAttempts] >= 0");
                    table.ForeignKey(
                        name: "FK_DeliveryOtps_DeliveryAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalSchema: "parcel",
                        principalTable: "DeliveryAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeliveryOtps_Parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalSchema: "parcel",
                        principalTable: "Parcels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAssignments_DeliveryAgentId_AssignedAtUtc",
                schema: "parcel",
                table: "DeliveryAssignments",
                columns: new[] { "DeliveryAgentId", "AssignedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryOtps_AssignmentId_CreatedAtUtc",
                schema: "parcel",
                table: "DeliveryOtps",
                columns: new[] { "AssignmentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryOtps_ParcelId",
                schema: "parcel",
                table: "DeliveryOtps",
                column: "ParcelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryOtps",
                schema: "parcel");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryAssignments_DeliveryAgentId_AssignedAtUtc",
                schema: "parcel",
                table: "DeliveryAssignments");
        }
    }
}
