using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepalParcel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7_Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "parcel",
                table: "PaymentTransactions",
                type: "nchar(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "NPR");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAtUtc",
                schema: "parcel",
                table: "PaymentTransactions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchantReference",
                schema: "parcel",
                table: "PaymentTransactions",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "RecordedByUserId",
                schema: "parcel",
                table: "PaymentTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CountedAmount",
                schema: "parcel",
                table: "CODSettlements",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TestGatewaySessions",
                schema: "parcel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MerchantReference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestGatewaySessions", x => x.Id);
                });

            // Any transaction that already exists gets a reference of its own before the unique index is built.
            migrationBuilder.Sql(
                """
                UPDATE [parcel].[PaymentTransactions]
                SET [MerchantReference] = 'LEG' + REPLACE(CONVERT(nvarchar(36), [Id]), '-', '')
                WHERE [MerchantReference] = ''
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_MerchantReference",
                schema: "parcel",
                table: "PaymentTransactions",
                column: "MerchantReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_Status_CreatedAtUtc",
                schema: "parcel",
                table: "PaymentTransactions",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TestGatewaySessions_MerchantReference",
                schema: "parcel",
                table: "TestGatewaySessions",
                column: "MerchantReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestGatewaySessions_ProviderTransactionId",
                schema: "parcel",
                table: "TestGatewaySessions",
                column: "ProviderTransactionId",
                unique: true,
                filter: "[ProviderTransactionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TestGatewaySessions",
                schema: "parcel");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_MerchantReference",
                schema: "parcel",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_Status_CreatedAtUtc",
                schema: "parcel",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "parcel",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                schema: "parcel",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "MerchantReference",
                schema: "parcel",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "RecordedByUserId",
                schema: "parcel",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "CountedAmount",
                schema: "parcel",
                table: "CODSettlements");
        }
    }
}
