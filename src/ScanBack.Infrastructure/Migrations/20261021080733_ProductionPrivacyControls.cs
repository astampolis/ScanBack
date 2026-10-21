using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScanBack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProductionPrivacyControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsentPolicyVersion",
                table: "RiderProfiles",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InvitationConsumedAt",
                table: "IncidentAccesses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionTokenHash",
                table: "IncidentAccesses",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdminBlocked",
                table: "EmergencyIdentifiers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ConsentPolicyVersion",
                table: "EmergencyContacts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotificationsConsentedAt",
                table: "EmergencyContacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotificationsRequested",
                table: "EmergencyContacts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PermissionVersion",
                table: "EmergencyContacts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublicPhoneConsentedAt",
                table: "EmergencyContacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicPhoneRequested",
                table: "EmergencyContacts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ConsentText",
                table: "ConsentRecords",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            // Preserve owner requests without inventing recipient consent. Legacy
            // permissions must be granted again using the versioned consent flow.
            migrationBuilder.Sql("""
                UPDATE [EmergencyContacts]
                SET [NotificationsRequested] = [NotifyEnabled],
                    [PublicPhoneRequested] = [PublicPhone],
                    [NotifyEnabled] = 0,
                    [PublicPhone] = 0,
                    [PermissionVersion] = NEWID();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentAccesses_SessionTokenHash",
                table: "IncidentAccesses",
                column: "SessionTokenHash",
                unique: true,
                filter: "[SessionTokenHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IncidentAccesses_SessionTokenHash",
                table: "IncidentAccesses");

            migrationBuilder.DropColumn(
                name: "ConsentPolicyVersion",
                table: "RiderProfiles");

            migrationBuilder.DropColumn(
                name: "InvitationConsumedAt",
                table: "IncidentAccesses");

            migrationBuilder.DropColumn(
                name: "SessionTokenHash",
                table: "IncidentAccesses");

            migrationBuilder.DropColumn(
                name: "IsAdminBlocked",
                table: "EmergencyIdentifiers");

            migrationBuilder.DropColumn(
                name: "ConsentPolicyVersion",
                table: "EmergencyContacts");

            migrationBuilder.DropColumn(
                name: "NotificationsConsentedAt",
                table: "EmergencyContacts");

            migrationBuilder.DropColumn(
                name: "NotificationsRequested",
                table: "EmergencyContacts");

            migrationBuilder.DropColumn(
                name: "PermissionVersion",
                table: "EmergencyContacts");

            migrationBuilder.DropColumn(
                name: "PublicPhoneConsentedAt",
                table: "EmergencyContacts");

            migrationBuilder.DropColumn(
                name: "PublicPhoneRequested",
                table: "EmergencyContacts");

            migrationBuilder.DropColumn(
                name: "ConsentText",
                table: "ConsentRecords");
        }
    }
}
