-- Run only against FintechStagDB. Covers the legacy alarm duplicate lookup.
IF DB_NAME() <> N'FintechStagDB'
    THROW 50001, 'Wrong database for ETL staging index', 1;

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
