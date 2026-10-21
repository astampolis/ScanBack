-- Only for IIS and SQL Server on the SAME Windows machine. Run the validating PowerShell wrapper.
-- For remote SQL, use separate domain/gMSA identities and adapt logins using the same DML role.
USE [master];
GO
IF DB_ID(N'$(DatabaseName)') IS NULL
    THROW 51000, 'Create the ScanBack database first with an approved owner, encryption and backup policy.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'IIS APPPOOL\$(AppPoolName)')
    CREATE LOGIN [IIS APPPOOL\$(AppPoolName)] FROM WINDOWS;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT SERVICE\ScanBack.Worker')
    CREATE LOGIN [NT SERVICE\ScanBack.Worker] FROM WINDOWS;
GO
USE [$(DatabaseName)];
GO
IF DATABASE_PRINCIPAL_ID(N'scanback_runtime') IS NULL
    CREATE ROLE [scanback_runtime] AUTHORIZATION [dbo];
-- This role can read/write application data but cannot CREATE/ALTER/DROP objects or administer security.
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [scanback_runtime];
IF DATABASE_PRINCIPAL_ID(N'IIS APPPOOL\$(AppPoolName)') IS NULL
    CREATE USER [IIS APPPOOL\$(AppPoolName)] FOR LOGIN [IIS APPPOOL\$(AppPoolName)];
IF DATABASE_PRINCIPAL_ID(N'NT SERVICE\ScanBack.Worker') IS NULL
    CREATE USER [NT SERVICE\ScanBack.Worker] FOR LOGIN [NT SERVICE\ScanBack.Worker];
IF IS_ROLEMEMBER(N'scanback_runtime', N'IIS APPPOOL\$(AppPoolName)') <> 1
    ALTER ROLE [scanback_runtime] ADD MEMBER [IIS APPPOOL\$(AppPoolName)];
IF IS_ROLEMEMBER(N'scanback_runtime', N'NT SERVICE\ScanBack.Worker') <> 1
    ALTER ROLE [scanback_runtime] ADD MEMBER [NT SERVICE\ScanBack.Worker];
GO
