-- Run in the database selected by FintechSqlConnectionString.
-- The timer also executes this idempotent script before scanning any inputs.
-- Existing audit rows and application data are not modified.
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.Fintech_EtlFileCheckpoints', N'U') IS NULL
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @lockResult int;
        EXEC @lockResult = sys.sp_getapplock
            @Resource = 'FintechEtl:checkpoint-schema', @LockMode = 'Exclusive',
            @LockOwner = 'Transaction', @LockTimeout = 15000;
        IF @lockResult < 0 THROW 51000, 'Could not acquire ETL checkpoint schema lock.', 1;
        IF OBJECT_ID(N'dbo.Fintech_EtlFileCheckpoints', N'U') IS NULL
            CREATE TABLE dbo.Fintech_EtlFileCheckpoints
            (
                KeyHash binary(32) NOT NULL CONSTRAINT PK_Fintech_EtlFileCheckpoints PRIMARY KEY,
                RemoteKey nvarchar(1280) NOT NULL,
                Status varchar(50) NOT NULL,
                ErrorMessage nvarchar(max) NULL,
                UpdatedOn datetime2(7) NOT NULL
            );
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
