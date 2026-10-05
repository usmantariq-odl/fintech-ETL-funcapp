using log4net;
using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;
using CsvHelper;
using CsvHelper.Configuration;
using System.Text;

namespace CSVReader
{
    class ReadTransactionTankData
    {
        ILog logger;
        DAL writeDB;

        public ReadTransactionTankData(ILog Log)
        {
            logger = Log;
            writeDB = new DAL(logger);
        }

    public void ReadTransactionData(string doc, OmniFintechMapping map, int? processedFileId = null)
    {
        //FtpConnectivity.isFileRegrenerated = false;
        //FtpConnectivity.isExceptionOccured = false;

        DataTable dt = GetDataTableFromCsv(doc, true);
        string fileName = Path.GetFileName(doc);
        Console.WriteLine("Path : " + doc);
        Console.WriteLine("FileName" + fileName);
        
        // Validate if file has columns and data
        if (dt == null || dt.Columns.Count == 0)
        {
            logger.Error($"File {fileName} is empty or has no columns. Cannot process.");
            Console.WriteLine($"ERROR: File {fileName} is empty or has no columns.");
            FtpConnectivity.isExceptionOccured = true;
            return;
        }
        
        Console.WriteLine("First Col of Sheet is " + dt.Columns[0].ColumnName);
        bool ownsProcessedFileRecord = !processedFileId.HasValue;
        int effectiveProcessedFileId = processedFileId ?? writeDB.EnsureProcessedFileRecord(doc, "Transactions");
        try
        {
            writeDB.AddDataToTransactions(dt, map, effectiveProcessedFileId);
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Processed");
        }
        catch (Exception ex)
        {
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Failed", ex.Message);
            throw;
        }

    }

    public void ReadTanksData(string doc, OmniFintechMapping map, int? processedFileId = null)
    {
        //FtpConnectivity.isFileRegrenerated = false;
        //FtpConnectivity.isExceptionOccured = false;
        DataTable dt = GetDataTableFromCsv(doc, true);
        //map.checkValidTankMapping(dt);
        //writeDB.AddDataToTanksADX(dt,map);

        string fileName = Path.GetFileName(doc);
        Console.WriteLine("Path : " + doc);
        Console.WriteLine("FileName" + fileName);
        
        // Validate if file has columns and data
        if (dt == null || dt.Columns.Count == 0)
        {
            logger.Error($"File {fileName} is empty or has no columns. Cannot process.");
            Console.WriteLine($"ERROR: File {fileName} is empty or has no columns.");
            FtpConnectivity.isExceptionOccured = true;
            return;
        }
        
        Console.WriteLine("First Col of Sheet is " + dt.Columns[0].ColumnName);
        bool ownsProcessedFileRecord = !processedFileId.HasValue;
        int effectiveProcessedFileId = processedFileId ?? writeDB.EnsureProcessedFileRecord(doc, "TankLevels");
        try
        {
            writeDB.AddDataToTanks(dt, map, effectiveProcessedFileId);
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Processed");
        }
        catch (Exception ex)
        {
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Failed", ex.Message);
            throw;
        }
    }
    public void ReadDeliveryData(string doc, OmniFintechMapping map, int? processedFileId = null)
    {
        //FtpConnectivity.isFileRegrenerated = false;
        //FtpConnectivity.isExceptionOccured = false;
        DataTable dt = GetDataTableFromCsv(doc, true);
        string fileName = Path.GetFileName(doc);
        Console.WriteLine("Path : " + doc);
        Console.WriteLine("FileName" + fileName);
        
        // Validate if file has columns and data
        if (dt == null || dt.Columns.Count == 0)
        {
            logger.Error($"File {fileName} is empty or has no columns. Cannot process.");
            Console.WriteLine($"ERROR: File {fileName} is empty or has no columns.");
            FtpConnectivity.isExceptionOccured = true;
            return;
        }
        
        Console.WriteLine("First Col of Sheet is " + dt.Columns[0].ColumnName);
        bool ownsProcessedFileRecord = !processedFileId.HasValue;
        int effectiveProcessedFileId = processedFileId ?? writeDB.EnsureProcessedFileRecord(doc, "TankDelivery");
        try
        {
            writeDB.AddDataToTDD(dt, map, effectiveProcessedFileId);
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Processed");
        }
        catch (Exception ex)
        {
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Failed", ex.Message);
            throw;
        }
    }

    public void ReadTransactionDetails(string doc, OmniFintechMapping map, int? processedFileId = null)
    {
        //FtpConnectivity.isFileRegrenerated = false;
        //FtpConnectivity.isExceptionOccured = false;
        DataTable dt = GetDataTableFromCsv(doc, true);

        string fileName = Path.GetFileName(doc);
        Console.WriteLine("Path : " + doc);
        Console.WriteLine("FileName" + fileName);
        
        // Validate if file has columns and data
        if (dt == null || dt.Columns.Count == 0)
        {
            logger.Error($"File {fileName} is empty or has no columns. Cannot process.");
            Console.WriteLine($"ERROR: File {fileName} is empty or has no columns.");
            FtpConnectivity.isExceptionOccured = true;
            return;
        }

        DataView dataView = dt.DefaultView;
        dt = dataView.ToTable(true, "site_id", "pay_sale_id", "pay_payment_type", "last_modified_date", "pay_payment_info");
        bool ownsProcessedFileRecord = !processedFileId.HasValue;
        int effectiveProcessedFileId = processedFileId ?? writeDB.EnsureProcessedFileRecord(doc, "TransactionsType");
        try
        {
            writeDB.AddDataToTransactionsType(dt, map, effectiveProcessedFileId);
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Processed");
        }
        catch (Exception ex)
        {
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Failed", ex.Message);
            throw;
        }
        //map.checkValidTransactionMapping(dt);
        //writeDB.AddDataToDeliveryADX(dt,map);
        Console.WriteLine("First Col of Sheet is " + dt.Columns[0].ColumnName);

    }
    public void ReadAlarmsDetails(string doc, OmniFintechMapping map, int? processedFileId = null)
    {
        DataTable dt = GetDataTableFromCsv(doc, true);

        string fileName = Path.GetFileName(doc);
        Console.WriteLine("Path : " + doc);
        Console.WriteLine("FileName : " + fileName);
        
        // Validate if file has columns and data
        if (dt == null || dt.Columns.Count == 0)
        {
            logger.Error($"File {fileName} is empty or has no columns. Cannot process.");
            Console.WriteLine($"ERROR: File {fileName} is empty or has no columns.");
            FtpConnectivity.isExceptionOccured = true;
            return;
        }

        // Keep only TAS columns
        DataView view = dt.DefaultView;
        dt = view.ToTable(true,
            "site_id",
            "alarm_type",
            "location_type",
            "location_id",
            "alarm_status",
            "alarm_date",
            "alarm_time",
            "last_modified_date",
            "severity",
            "ack_user",
            "ack_date",
            "ack_time",
            "auto_reset"
        );

        bool ownsProcessedFileRecord = !processedFileId.HasValue;
        int effectiveProcessedFileId = processedFileId ?? writeDB.EnsureProcessedFileRecord(doc, "Alarms");
        try
        {
            writeDB.AddDataToAlarms(dt, map, effectiveProcessedFileId);
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Processed");
        }
        catch (Exception ex)
        {
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Failed", ex.Message);
            throw;
        }

        Console.WriteLine("First Col of Sheet is " + dt.Columns[0].ColumnName);
    }


    public void ReadMagLinkStockLevelData(string doc, OmniFintechMapping map, int? processedFileId = null)
    {
        Console.WriteLine($"Reading data from: {doc}");
        DataTable dt = GetDataTableFromCsv(doc, true);
        
        string fileName = Path.GetFileName(doc);
        
        // Validate if file has columns and data
        if (dt == null || dt.Columns.Count == 0)
        {
            logger.Error($"File {fileName} is empty or has no columns. Cannot process.");
            Console.WriteLine($"ERROR: File {fileName} is empty or has no columns.");
            FtpConnectivity.isExceptionOccured = true;
            return;
        }
        
        bool ownsProcessedFileRecord = !processedFileId.HasValue;
        int effectiveProcessedFileId = processedFileId ?? writeDB.EnsureProcessedFileRecord(doc, "MagLinkStockLevels");
        try
        {
            writeDB.AddDataToTanksForMagLink(dt, map, effectiveProcessedFileId);
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Processed");
        }
        catch (Exception ex)
        {
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Failed", ex.Message);
            throw;
        }
    }

    public void ReadMagLinkDeliveryData(string doc, OmniFintechMapping map, int? processedFileId = null)
    {
        Console.WriteLine($"Reading data from: {doc}");
        DataTable dt = GetDataTableFromCsv(doc, true);
        
        string fileName = Path.GetFileName(doc);
        
        // Validate if file has columns and data
        if (dt == null || dt.Columns.Count == 0)
        {
            logger.Error($"File {fileName} is empty or has no columns. Cannot process.");
            Console.WriteLine($"ERROR: File {fileName} is empty or has no columns.");
            FtpConnectivity.isExceptionOccured = true;
            return;
        }
        
        bool ownsProcessedFileRecord = !processedFileId.HasValue;
        int effectiveProcessedFileId = processedFileId ?? writeDB.EnsureProcessedFileRecord(doc, "MagLinkDeliveries");
        try
        {
            writeDB.AddDataToTankDeliveryForMagLink(dt, map, effectiveProcessedFileId);
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Processed");
        }
        catch (Exception ex)
        {
            if (ownsProcessedFileRecord)
                writeDB.UpdateProcessedFileStatus(effectiveProcessedFileId, "Failed", ex.Message);
            throw;
        }
    }

    public DataTable GetDataTableFromCsv(string path, bool isFirstRowHeader)
    {
        try
        {
            // Check if file exists
            if (!File.Exists(path))
            {
                logger.Error($"File {path} does not exist.");
                FtpConnectivity.isExceptionOccured = true;
                return new DataTable();
            }

            // Check if file is empty
            FileInfo fileInfo = new FileInfo(path);
            if (fileInfo.Length == 0)
            {
                logger.Error($"File {Path.GetFileName(path)} is empty (0 bytes).");
                FtpConnectivity.isExceptionOccured = true;
                return new DataTable();
            }

            string csvFileText = File.ReadAllText(path);
            if (csvFileText.Contains("regenerated"))
            {
                logger.Info("Filename: " + Path.GetFileName(path) + " is Regenerated");

                string[] lines = File.ReadAllLines(path);
                string[] updatedLines = lines.Skip(1).ToArray();

                //csvFileText = csvFileText.Replace("regenerated,", "").Trim();
                File.WriteAllLines(path, updatedLines);
                FtpConnectivity.isFileRegrenerated = true;
            }

            // Use CsvHelper to read the CSV file
            DataTable dataTable = new DataTable();
            dataTable.Locale = CultureInfo.CurrentCulture;

            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = isFirstRowHeader,
                MissingFieldFound = null,
                BadDataFound = null,
                TrimOptions = TrimOptions.Trim
            };

            using (var reader = new StreamReader(path, Encoding.UTF8))
            using (var csv = new CsvReader(reader, config))
            {
                // Read the header
                csv.Read();
                csv.ReadHeader();
                
                // Add columns to DataTable - all as string type to avoid type inference issues
                if (isFirstRowHeader)
                {
                    foreach (var header in csv.HeaderRecord)
                    {
                        dataTable.Columns.Add(header, typeof(string));
                    }
                }

                // Read all rows
                while (csv.Read())
                {
                    var row = dataTable.NewRow();
                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        try
                        {
                            // Get field as string to preserve all values (numeric and text)
                            row[i] = csv.GetField(i);
                        }
                        catch
                        {
                            row[i] = DBNull.Value;
                        }
                    }
                    dataTable.Rows.Add(row);
                }
            }

            return dataTable;
        }
        catch (Exception ex)
        {
            logger.Error($"Error reading CSV file {Path.GetFileName(path)}: {ex.Message}", ex);
            FtpConnectivity.isExceptionOccured = true;
            return new DataTable();
        }
    }
        public StringReader getXmlString(XDocument doc)
        {
            var stringReader = new StringReader(doc.ToString());
            return stringReader;
        }
        public DataTable getXmlDataSet(DataSet ds)
        {
            return ds.Tables[0];
        }
    }
}




