IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetRoles] (
        [Id] nvarchar(450) NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUsers] (
        [Id] nvarchar(450) NOT NULL,
        [IsActive] bit NOT NULL,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditEvents] (
        [Id] uniqueidentifier NOT NULL,
        [ActorId] nvarchar(450) NULL,
        [Action] nvarchar(100) NOT NULL,
        [EntityId] nvarchar(128) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [PlatformSettings] (
        [Id] int NOT NULL,
        [IncidentRetentionDays] int NOT NULL,
        [IncidentAccessHours] int NOT NULL,
        [NotificationCooldownMinutes] int NOT NULL,
        [NotificationsEnabled] bit NOT NULL,
        CONSTRAINT [PK_PlatformSettings] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_PlatformSettings_Access] CHECK ([IncidentAccessHours] BETWEEN 1 AND 168),
        CONSTRAINT [CK_PlatformSettings_Cooldown] CHECK ([NotificationCooldownMinutes] BETWEEN 1 AND 1440),
        CONSTRAINT [CK_PlatformSettings_Retention] CHECK ([IncidentRetentionDays] BETWEEN 1 AND 365),
        CONSTRAINT [CK_PlatformSettings_Singleton] CHECK ([Id] = 1)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserLogins] (
        [LoginProvider] nvarchar(450) NOT NULL,
        [ProviderKey] nvarchar(450) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserRoles] (
        [UserId] nvarchar(450) NOT NULL,
        [RoleId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserTokens] (
        [UserId] nvarchar(450) NOT NULL,
        [LoginProvider] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [ConsentRecords] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] nvarchar(450) NOT NULL,
        [PolicyVersion] nvarchar(64) NOT NULL,
        [Action] nvarchar(64) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ConsentRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ConsentRecords_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [RiderProfiles] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] nvarchar(450) NOT NULL,
        [FullName] nvarchar(180) NOT NULL,
        [DateOfBirth] date NULL,
        [PhotoFileName] nvarchar(200) NULL,
        [BloodType] nvarchar(max) NULL,
        [Allergies] nvarchar(max) NULL,
        [Conditions] nvarchar(max) NULL,
        [Medications] nvarchar(max) NULL,
        [OtherInformation] nvarchar(max) NULL,
        [Instructions] nvarchar(max) NULL,
        [PublicFields] int NOT NULL,
        [HealthConsent] bit NOT NULL,
        [ConsentAt] datetimeoffset NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_RiderProfiles] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RiderProfiles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [EmergencyContacts] (
        [Id] uniqueidentifier NOT NULL,
        [RiderProfileId] uniqueidentifier NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [Relationship] nvarchar(80) NOT NULL,
        [Email] nvarchar(254) NOT NULL,
        [Phone] nvarchar(40) NOT NULL,
        [PublicPhone] bit NOT NULL,
        [NotifyEnabled] bit NOT NULL,
        [IsVerified] bit NOT NULL,
        [VerificationTokenHash] varchar(64) NOT NULL,
        [VerificationExpiresAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_EmergencyContacts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EmergencyContacts_RiderProfiles_RiderProfileId] FOREIGN KEY ([RiderProfileId]) REFERENCES [RiderProfiles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [EmergencyIdentifiers] (
        [Id] uniqueidentifier NOT NULL,
        [RiderProfileId] uniqueidentifier NOT NULL,
        [Label] nvarchar(80) NOT NULL,
        [TokenHash] varchar(64) NOT NULL,
        [ProtectedToken] nvarchar(1024) NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        CONSTRAINT [PK_EmergencyIdentifiers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EmergencyIdentifiers_RiderProfiles_RiderProfileId] FOREIGN KEY ([RiderProfileId]) REFERENCES [RiderProfiles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [Incidents] (
        [Id] uniqueidentifier NOT NULL,
        [RiderProfileId] uniqueidentifier NOT NULL,
        [EmergencyIdentifierId] uniqueidentifier NOT NULL,
        [ReportedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [Latitude] float NULL,
        [Longitude] float NULL,
        [LocationDescription] nvarchar(500) NULL,
        [Consciousness] nvarchar(32) NOT NULL,
        [PeopleInvolved] int NOT NULL,
        [SubmissionKey] varchar(128) NOT NULL,
        [IsClosed] bit NOT NULL,
        CONSTRAINT [PK_Incidents] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Incidents_Coordinates] CHECK (([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] IS NOT NULL AND [Longitude] IS NOT NULL AND [Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180)),
        CONSTRAINT [CK_Incidents_PeopleInvolved] CHECK ([PeopleInvolved] BETWEEN 1 AND 100),
        CONSTRAINT [FK_Incidents_EmergencyIdentifiers_EmergencyIdentifierId] FOREIGN KEY ([EmergencyIdentifierId]) REFERENCES [EmergencyIdentifiers] ([Id]),
        CONSTRAINT [FK_Incidents_RiderProfiles_RiderProfileId] FOREIGN KEY ([RiderProfileId]) REFERENCES [RiderProfiles] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [IncidentAccesses] (
        [Id] uniqueidentifier NOT NULL,
        [IncidentId] uniqueidentifier NOT NULL,
        [EmergencyContactId] uniqueidentifier NULL,
        [Role] nvarchar(32) NOT NULL,
        [TokenHash] varchar(64) NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [IsRevoked] bit NOT NULL,
        CONSTRAINT [PK_IncidentAccesses] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_IncidentAccesses_EmergencyContacts_EmergencyContactId] FOREIGN KEY ([EmergencyContactId]) REFERENCES [EmergencyContacts] ([Id]),
        CONSTRAINT [FK_IncidentAccesses_Incidents_IncidentId] FOREIGN KEY ([IncidentId]) REFERENCES [Incidents] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [Id] uniqueidentifier NOT NULL,
        [IncidentId] uniqueidentifier NULL,
        [RiderProfileId] uniqueidentifier NULL,
        [EmergencyContactId] uniqueidentifier NULL,
        [ToEmail] nvarchar(254) NOT NULL,
        [Subject] nvarchar(300) NOT NULL,
        [Body] nvarchar(max) NOT NULL,
        [Kind] nvarchar(32) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [NextAttemptAt] datetimeoffset NOT NULL,
        [AcceptedAt] datetimeoffset NULL,
        [Attempts] int NOT NULL,
        [LastError] nvarchar(80) NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OutboxMessages_EmergencyContacts_EmergencyContactId] FOREIGN KEY ([EmergencyContactId]) REFERENCES [EmergencyContacts] ([Id]),
        CONSTRAINT [FK_OutboxMessages_Incidents_IncidentId] FOREIGN KEY ([IncidentId]) REFERENCES [Incidents] ([Id]),
        CONSTRAINT [FK_OutboxMessages_RiderProfiles_RiderProfileId] FOREIGN KEY ([RiderProfileId]) REFERENCES [RiderProfiles] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_CreatedAt] ON [AuditEvents] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ConsentRecords_UserId_CreatedAt] ON [ConsentRecords] ([UserId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_EmergencyContacts_RiderProfileId] ON [EmergencyContacts] ([RiderProfileId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_EmergencyContacts_VerificationTokenHash] ON [EmergencyContacts] ([VerificationTokenHash]) WHERE [VerificationTokenHash] <> ''''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_EmergencyIdentifiers_RiderProfileId] ON [EmergencyIdentifiers] ([RiderProfileId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmergencyIdentifiers_TokenHash] ON [EmergencyIdentifiers] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_IncidentAccesses_EmergencyContactId] ON [IncidentAccesses] ([EmergencyContactId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_IncidentAccesses_ExpiresAt] ON [IncidentAccesses] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_IncidentAccesses_IncidentId] ON [IncidentAccesses] ([IncidentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_IncidentAccesses_TokenHash] ON [IncidentAccesses] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Incidents_EmergencyIdentifierId] ON [Incidents] ([EmergencyIdentifierId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Incidents_ExpiresAt] ON [Incidents] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Incidents_RiderProfileId_ReportedAt] ON [Incidents] ([RiderProfileId], [ReportedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Incidents_SubmissionKey] ON [Incidents] ([SubmissionKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_AcceptedAt_NextAttemptAt] ON [OutboxMessages] ([AcceptedAt], [NextAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_CreatedAt] ON [OutboxMessages] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_EmergencyContactId] ON [OutboxMessages] ([EmergencyContactId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_IncidentId] ON [OutboxMessages] ([IncidentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_RiderProfileId] ON [OutboxMessages] ([RiderProfileId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RiderProfiles_UserId] ON [RiderProfiles] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014214153_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261014214153_InitialCreate', N'10.0.13');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [RiderProfiles] ADD [ConsentPolicyVersion] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [IncidentAccesses] ADD [InvitationConsumedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [IncidentAccesses] ADD [SessionTokenHash] varchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [EmergencyIdentifiers] ADD [IsAdminBlocked] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [EmergencyContacts] ADD [ConsentPolicyVersion] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [EmergencyContacts] ADD [NotificationsConsentedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [EmergencyContacts] ADD [NotificationsRequested] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [EmergencyContacts] ADD [PermissionVersion] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [EmergencyContacts] ADD [PublicPhoneConsentedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [EmergencyContacts] ADD [PublicPhoneRequested] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    ALTER TABLE [ConsentRecords] ADD [ConsentText] nvarchar(4000) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    UPDATE [EmergencyContacts]
    SET [NotificationsRequested] = [NotifyEnabled],
        [PublicPhoneRequested] = [PublicPhone],
        [NotifyEnabled] = 0,
        [PublicPhone] = 0,
        [PermissionVersion] = NEWID();
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_IncidentAccesses_SessionTokenHash] ON [IncidentAccesses] ([SessionTokenHash]) WHERE [SessionTokenHash] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021080733_ProductionPrivacyControls'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261021080733_ProductionPrivacyControls', N'10.0.13');
END;

COMMIT;
GO
