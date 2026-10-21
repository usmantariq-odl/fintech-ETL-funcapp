-- Manually select the ETL database configured in FintechSqlConnectionString before running.
-- Covers the legacy alarm duplicate lookup; this script is not run by the Function App.
IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50001, 'Select the intended ETL application database before running this index script', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Fintech_Alarms')
      AND name = N'IX_Fintech_Alarms_Site_FcDate'
)
BEGIN
    CREATE INDEX IX_Fintech_Alarms_Site_FcDate
    ON dbo.Fintech_Alarms (Site_ID, fc_alarmdatetime)
    INCLUDE (Alarm_Type, Location_Type, Location_ID);
END;
