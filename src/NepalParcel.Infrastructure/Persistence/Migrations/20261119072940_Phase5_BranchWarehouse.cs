using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepalParcel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5_BranchWarehouse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NextWarehouseId",
                schema: "parcel",
                table: "Parcels",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAtUtc",
                schema: "parcel",
                table: "Parcels",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Parcels that already exist were given the "zero" date above. Their status last changed when the latest history row was
            // written (or, if there is none, when the parcel was created), so waiting times stay meaningful after the upgrade.
            migrationBuilder.Sql(
                """
                UPDATE p
                SET StatusChangedAtUtc = COALESCE(
                    (SELECT MAX(h.OccurredAtUtc) FROM [parcel].[ParcelStatusHistory] AS h WHERE h.ParcelId = p.Id),
                    p.CreatedAtUtc)
                FROM [parcel].[Parcels] AS p
                """);

            migrationBuilder.CreateTable(
                name: "ParcelMovements",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParcelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScanType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ToWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StaffUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsUnexpectedArrival = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParcelMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParcelMovements_Parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalSchema: "parcel",
                        principalTable: "Parcels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParcelMovements_Users_StaffUserId",
                        column: x => x.StaffUserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParcelMovements_Warehouses_ToWarehouseId",
                        column: x => x.ToWarehouseId,
                        principalSchema: "parcel",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParcelMovements_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "parcel",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffAssignments",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffAssignments_Branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "parcel",
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffAssignments_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "parcel",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ParcelScans",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParcelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ScanType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ScannedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParcelScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParcelScans_ParcelMovements_MovementId",
                        column: x => x.MovementId,
                        principalSchema: "parcel",
                        principalTable: "ParcelMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParcelScans_Parcels_ParcelId",
                        column: x => x.ParcelId,
                        principalSchema: "parcel",
                        principalTable: "Parcels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParcelScans_Users_StaffUserId",
                        column: x => x.StaffUserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParcelScans_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "parcel",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Parcels_CurrentStatusId_StatusChangedAtUtc",
                schema: "parcel",
                table: "Parcels",
                columns: new[] { "CurrentStatusId", "StatusChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Parcels_NextWarehouseId",
                schema: "parcel",
                table: "Parcels",
                column: "NextWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_ParcelMovements_ParcelId_OccurredAtUtc",
                schema: "parcel",
                table: "ParcelMovements",
                columns: new[] { "ParcelId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParcelMovements_StaffUserId",
                schema: "parcel",
                table: "ParcelMovements",
                column: "StaffUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ParcelMovements_ToWarehouseId",
                schema: "parcel",
                table: "ParcelMovements",
                column: "ToWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_ParcelMovements_WarehouseId_OccurredAtUtc",
                schema: "parcel",
                table: "ParcelMovements",
                columns: new[] { "WarehouseId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParcelScans_MovementId",
                schema: "parcel",
                table: "ParcelScans",
                column: "MovementId");

            migrationBuilder.CreateIndex(
                name: "IX_ParcelScans_ParcelId_ScannedAtUtc",
                schema: "parcel",
                table: "ParcelScans",
                columns: new[] { "ParcelId", "ScannedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParcelScans_StaffUserId_ScannedAtUtc",
                schema: "parcel",
                table: "ParcelScans",
                columns: new[] { "StaffUserId", "ScannedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParcelScans_WarehouseId_ScannedAtUtc",
                schema: "parcel",
                table: "ParcelScans",
                columns: new[] { "WarehouseId", "ScannedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAssignments_BranchId_EndedAtUtc",
                schema: "parcel",
                table: "StaffAssignments",
                columns: new[] { "BranchId", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAssignments_UserId",
                schema: "parcel",
                table: "StaffAssignments",
                column: "UserId",
                unique: true,
                filter: "[EndedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffAssignments_WarehouseId",
                schema: "parcel",
                table: "StaffAssignments",
                column: "WarehouseId");

            migrationBuilder.AddForeignKey(
                name: "FK_Parcels_Warehouses_NextWarehouseId",
                schema: "parcel",
                table: "Parcels",
                column: "NextWarehouseId",
                principalSchema: "parcel",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Parcels_Warehouses_NextWarehouseId",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropTable(
                name: "ParcelScans",
                schema: "parcel");

            migrationBuilder.DropTable(
                name: "StaffAssignments",
                schema: "parcel");

            migrationBuilder.DropTable(
                name: "ParcelMovements",
                schema: "parcel");

            migrationBuilder.DropIndex(
                name: "IX_Parcels_CurrentStatusId_StatusChangedAtUtc",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropIndex(
                name: "IX_Parcels_NextWarehouseId",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "NextWarehouseId",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "StatusChangedAtUtc",
                schema: "parcel",
                table: "Parcels");
        }
    }
}
