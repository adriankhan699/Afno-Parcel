using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepalParcel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3_ParcelBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BaseCharge",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ChargeableWeightKg",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(10,3)",
                precision: 10,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CodFee",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "HeightCm",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LengthCm",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "PricingRuleId",
                schema: "parcel",
                table: "Parcels",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                schema: "parcel",
                table: "Parcels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReceiverAddressInstructions",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderAddressDistrict",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SenderAddressInstructions",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderAddressLandmark",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderAddressMunicipality",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SenderAddressPostalCode",
                schema: "parcel",
                table: "Parcels",
                type: "nchar(5)",
                fixedLength: true,
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderAddressProvince",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SenderAddressStreetOrTole",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SenderAddressWardNumber",
                schema: "parcel",
                table: "Parcels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SenderName",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SenderPhone",
                schema: "parcel",
                table: "Parcels",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "VatAmount",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VatPercent",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VolumetricWeightKg",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(10,3)",
                precision: 10,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "WeightCharge",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "WidthCm",
                schema: "parcel",
                table: "Parcels",
                type: "decimal(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parcels_BookedAtUtc",
                schema: "parcel",
                table: "Parcels",
                column: "BookedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Parcels_PricingRuleId",
                schema: "parcel",
                table: "Parcels",
                column: "PricingRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Parcels_SenderPhone",
                schema: "parcel",
                table: "Parcels",
                column: "SenderPhone");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parcels_Booking",
                schema: "parcel",
                table: "Parcels",
                sql: "[Quantity] > 0 AND [VolumetricWeightKg] >= 0 AND [ChargeableWeightKg] > 0 AND [BaseCharge] >= 0 AND [WeightCharge] >= 0 AND [CodFee] >= 0 AND [VatAmount] >= 0");

            migrationBuilder.AddForeignKey(
                name: "FK_Parcels_PricingRules_PricingRuleId",
                schema: "parcel",
                table: "Parcels",
                column: "PricingRuleId",
                principalSchema: "parcel",
                principalTable: "PricingRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Parcels_PricingRules_PricingRuleId",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropIndex(
                name: "IX_Parcels_BookedAtUtc",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropIndex(
                name: "IX_Parcels_PricingRuleId",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropIndex(
                name: "IX_Parcels_SenderPhone",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parcels_Booking",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "BaseCharge",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "ChargeableWeightKg",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "CodFee",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "HeightCm",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "LengthCm",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "PricingRuleId",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "Quantity",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "ReceiverAddressInstructions",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressDistrict",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressInstructions",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressLandmark",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressMunicipality",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressPostalCode",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressProvince",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressStreetOrTole",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderAddressWardNumber",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderName",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "SenderPhone",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "VatAmount",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "VatPercent",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "VolumetricWeightKg",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "WeightCharge",
                schema: "parcel",
                table: "Parcels");

            migrationBuilder.DropColumn(
                name: "WidthCm",
                schema: "parcel",
                table: "Parcels");
        }
    }
}
