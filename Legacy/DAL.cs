using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using log4net;
using CSVReader.models;

namespace CSVReader
{
    class DAL
    {
        public static string connectToDb = "";
        public static string secondaryDbConnectionString = "";
        public static bool enableDualInsert = false;

        private const string URL = "http://fintechpythoniotpublisher.azurewebsites.net/sendJSON/";



        public string InsertIntoTransactions = "INSERT INTO Fintech_Transactions" +
                    "([Site_ID],[Sale_ID],[End_Date],[End_Time],[Pump_ID],[Hose_ID],[Grade_ID],[Volume],[Money],[PPU],[Level],[Sale_Type],[Initial_Volume]" +
                    ",[Final_Volume],[Start_Date],[Start_Time],[Preset_Amount],[Sale_Auth],[Authorizer],[Article],[FC_SiteID], [FC_StartDateTime],[FC_EndDateTime],[CreatedOn])" +
                    " select * from @TransactionsTable";


        public string InsertIntoTanks = "INSERT INTO Fintech_TankLevels" +
             "([Site_ID],[Tank_ID],[Product_ID],[Product_Number],[Product_Volume],[Product_Height],[Water_Volume],[Water_Height],[Product_Temperature],[Date_Last_Read],[Time_Last_Read]" +
            ",[Last_Trans_ID],[Accum_Volume],[IsAccum],[Delivery_ini_Product_Volume],[Delivery_ini_Product_Height],[Delivery_ini_Water_Volume],[Delivery_ini_Water_Height]," +
            "[Delivery_Start_Date],[Delivery_Start_Time],[Probe_Status],[delivery_ini_temp],[TC_Volume],[Date_Last_Leak_Test],[Time_Last_Leak_Test],[Last_Modified_Date],[Product_Density]," +
            "[Product_Weight],[Ullage],[Delivered_Volume],[Delivery_State],[prod_tc_density],[Time_Stamp],[CreatedOn],[FC_SiteID],[Omni_AssetID],[FC_TimeStamp])" +
            "VALUES(";


        public string InsertIntoTDD = "INSERT INTO Fintech_TankDelivery" + "([site_id],[tank_id],[date_start],[time_start],[vol_total]," +
            "[date_end]," +
            "[time_end],[vol_start],[vol_end],[height_start],[height_end],[temp_start],[temp_end],[transactions_volume],[water_start],[water_end]," +
            "[water_height_start],[water_height_end],[delivery_id],[delivery_note_id],[vol_tc_total],[vol_tc_start],[vol_tc_end]," +
            "[last_modified_date],[source_type],[prod_density_start],[prod_tc_density_start],[prod_density_end],[prod_tc_density_end])Values(";


        public string queryForOmniSiteId = "Select Omni_SiteID from Omni_Fintech_SiteMapping where FC_SiteID =";

        public string queryForTankProductID = "Select ProdID_FK from Omni_Fintech_TankMapping where Omni_AssetID = ";

        public string queryForArticleName = "Select product_name from Omni_Fintech_Products where OmniCustomerID_FK = {0} and product_id  = {1}";
        public string queryForCustomerID = "Select Customer_Id_FK from Plant_Site where Site_Id = {0}";

        //connecting to IOT HUB
        /* key to Connect Go Database */
        //private static DeviceClient s_deviceClient_go;

        /*key to connect to Total Database */
        //private static DeviceClient s_deviceClient_total;

        ILog logger;
        public DAL(ILog Log)
        {
            logger = Log;
            
            // Read primary database connection string from config
            connectToDb = EtlSettings.Load().SqlConnectionString;
            if (string.IsNullOrEmpty(connectToDb))
            {
                logger.Error("FintechSqlConnectionString is missing.");
                throw new InvalidOperationException("FintechSqlConnectionString is missing.");
            }
            logger.Info("Primary database connection string loaded from config.");
            
            // Stress testing must write to one database only.
            enableDualInsert = false;
            secondaryDbConnectionString = string.Empty;

            // Ensure dups directory exists
            try
            {
                if (!Directory.Exists(Path.Combine(Path.GetTempPath(), "FintechEtl", "dups")))
                {
                    Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FintechEtl", "dups"));
                    logger.Info("Created 'dups' directory for duplicate records");
                }
            }
            catch (Exception ex)
            {
                    if (ex is SqlException) throw;
                logger.Error($"Failed to create 'dups' directory: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Safely writes duplicate records to CSV file with error handling
        /// </summary>
        private void WriteDuplicateToCSV(string fileName, string data, string recordType)
        {
            try
            {
                string filePath = Path.Combine(Path.Combine(Path.GetTempPath(), "FintechEtl", "dups"), fileName);
                using (FileStream fs = new FileStream(filePath, System.IO.FileMode.Append, FileAccess.Write))
                {
                    using (StreamWriter sw = new StreamWriter(fs))
                    {
                        sw.WriteLine(data);
                    }
                }
                logger.Info($"Duplicate {recordType} record written to {fileName}");
            }
            catch (IOException ioEx)
            {
                logger.Error($"IOException writing duplicate {recordType} to {fileName}: {ioEx.Message}", ioEx);
            }
            catch (UnauthorizedAccessException authEx)
            {
                logger.Error($"Access denied writing duplicate {recordType} to {fileName}: {authEx.Message}", authEx);
            }
            catch (Exception ex)
            {
                    if (ex is SqlException) throw;
                logger.Error($"Unexpected error writing duplicate {recordType} to {fileName}: {ex.Message}", ex);
            }
        }

        public string GetProcessedFileStatus(string remotePath)
        {
            using (var con = new SqlConnection(connectToDb))
            using (var cmd = new SqlCommand(@"SELECT TOP 1 Status FROM Fintech_ProcessedFiles
                WHERE [Path] IN (@Path, @LegacyPath, @PreviousPath, @PreviousLegacyPath) AND FileName = @FileName ORDER BY ID DESC", con))
            {
                var key = RemoteFileKey(remotePath);
                cmd.Parameters.Add("@Path", SqlDbType.VarChar, 1000).Value = key.Directory;
                cmd.Parameters.Add("@LegacyPath", SqlDbType.VarChar, 1000).Value = key.Directory.Replace('/', '\\');
                var previousDirectory = SftpLayout.PreviousInputDirectory(key.Directory);
                cmd.Parameters.Add("@PreviousPath", SqlDbType.VarChar, 1000).Value = previousDirectory;
                cmd.Parameters.Add("@PreviousLegacyPath", SqlDbType.VarChar, 1000).Value = previousDirectory.Replace('/', '\\');
                cmd.Parameters.Add("@FileName", SqlDbType.VarChar, 255).Value = key.Name;
                con.Open();
                return cmd.ExecuteScalar() as string;
            }
        }

        internal static (string Directory, string Name) RemoteFileKey(string remotePath)
        {
            var normalized = remotePath.Replace('\\', '/');
            var separator = normalized.LastIndexOf('/');
            var directory = separator < 0 ? string.Empty : normalized[..separator];
            var name = normalized[(separator + 1)..];
            if (directory.Length > 1000 || name.Length > 255)
                throw new InvalidDataException("File identity exceeds Fintech_ProcessedFiles column limits.");
            return (directory, name);
        }

        public int EnsureProcessedFileRecord(string filePath, string fileType, string remotePath = null)
        {
            string fullPath = Path.GetFullPath(filePath);
            var key = remotePath == null ? (Path.GetDirectoryName(fullPath) ?? string.Empty, Path.GetFileName(fullPath)) : RemoteFileKey(remotePath);
            string directoryPath = key.Item1;
            string fileName = key.Item2;
            long fileSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;

            using (SqlConnection con = new SqlConnection(connectToDb))
            {
                con.Open();

                using (SqlCommand existingCmd = new SqlCommand(@"
                    SELECT TOP 1 ID
                    FROM Fintech_ProcessedFiles
                    WHERE [Path] IN (@Path, @LegacyPath, @PreviousPath, @PreviousLegacyPath) AND FileName = @FileName
                    ORDER BY ID DESC;", con))
                {
                    existingCmd.Parameters.Add("@Path", SqlDbType.VarChar, 1000).Value = directoryPath;
                    existingCmd.Parameters.Add("@LegacyPath", SqlDbType.VarChar, 1000).Value = directoryPath.Replace('/', '\\');
                    var previousDirectory = SftpLayout.PreviousInputDirectory(directoryPath);
                    existingCmd.Parameters.Add("@PreviousPath", SqlDbType.VarChar, 1000).Value = previousDirectory;
                    existingCmd.Parameters.Add("@PreviousLegacyPath", SqlDbType.VarChar, 1000).Value = previousDirectory.Replace('/', '\\');
                    existingCmd.Parameters.Add("@FileName", SqlDbType.VarChar, 255).Value = fileName;

                    object existingId = existingCmd.ExecuteScalar();
                    if (existingId != null && existingId != DBNull.Value)
                    {
                        int processedFileId = Convert.ToInt32(existingId);

                        using (SqlCommand updateCmd = new SqlCommand(@"
                            UPDATE Fintech_ProcessedFiles
                            SET FileType = @FileType,
                                FileSize = @FileSize,
                                UpdatedOn = @UpdatedOn
                            WHERE ID = @ID;", con))
                        {
                            updateCmd.Parameters.Add("@ID", SqlDbType.Int).Value = processedFileId;
                            updateCmd.Parameters.Add("@FileType", SqlDbType.VarChar, 100).Value = (object)fileType ?? DBNull.Value;
                            updateCmd.Parameters.Add("@FileSize", SqlDbType.BigInt).Value = fileSize;
                            updateCmd.Parameters.Add("@UpdatedOn", SqlDbType.DateTime).Value = DateTime.Now.AddHours(5);
                            updateCmd.ExecuteNonQuery();
                        }

                        return processedFileId;
                    }
                }

                using (SqlCommand insertCmd = new SqlCommand(@"
                    INSERT INTO Fintech_ProcessedFiles([Path], FileName, FileType, Status, CreatedOn, UpdatedOn, FileSize, ErrorMessage)
                    OUTPUT INSERTED.ID
                    VALUES(@Path, @FileName, @FileType, @Status, @CreatedOn, @UpdatedOn, @FileSize, @ErrorMessage);", con))
                {
                    insertCmd.Parameters.Add("@Path", SqlDbType.VarChar, 1000).Value = directoryPath;
                    insertCmd.Parameters.Add("@FileName", SqlDbType.VarChar, 255).Value = fileName;
                    insertCmd.Parameters.Add("@FileType", SqlDbType.VarChar, 100).Value = (object)fileType ?? DBNull.Value;
                    insertCmd.Parameters.Add("@Status", SqlDbType.VarChar, 50).Value = "Processing";
                    insertCmd.Parameters.Add("@CreatedOn", SqlDbType.DateTime).Value = DateTime.Now.AddHours(5);
                    insertCmd.Parameters.Add("@UpdatedOn", SqlDbType.DateTime).Value = DateTime.Now.AddHours(5);
                    insertCmd.Parameters.Add("@FileSize", SqlDbType.BigInt).Value = fileSize;
                    insertCmd.Parameters.Add("@ErrorMessage", SqlDbType.VarChar, -1).Value = DBNull.Value;

                    return Convert.ToInt32(insertCmd.ExecuteScalar());
                }
            }
        }

        public void UpdateProcessedFileStatus(int processedFileId, string status, string errorMessage = null)
        {
            using (SqlConnection con = new SqlConnection(connectToDb))
            using (SqlCommand cmd = new SqlCommand(@"
                UPDATE Fintech_ProcessedFiles
                SET Status = @Status,
                    UpdatedOn = @UpdatedOn,
                    ErrorMessage = @ErrorMessage
                WHERE ID = @ID;", con))
            {
                cmd.Parameters.Add("@ID", SqlDbType.Int).Value = processedFileId;
                cmd.Parameters.Add("@Status", SqlDbType.VarChar, 50).Value = (object)status ?? DBNull.Value;
                cmd.Parameters.Add("@UpdatedOn", SqlDbType.DateTime).Value = DateTime.Now.AddHours(5);
                cmd.Parameters.Add("@ErrorMessage", SqlDbType.VarChar, -1).Value = string.IsNullOrWhiteSpace(errorMessage) ? (object)DBNull.Value : errorMessage;

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public List<int> getDeliveryIDs(int omniSiteID, int sDid, int eDid)
        {
            using SqlConnection con = new SqlConnection();
            SqlCommand cmd = new SqlCommand("Select delivery_id from Fintech_TankDelivery where Site_ID = @Omni_siteid and delivery_id >= @s_deliveryid and delivery_id <= @e_deliveryid", con);
            cmd.Parameters.AddWithValue("@Omni_siteid", omniSiteID);
            cmd.Parameters.AddWithValue("@s_deliveryid", sDid);
            cmd.Parameters.AddWithValue("@e_deliveryid", eDid);
            con.ConnectionString = (connectToDb);
            con.Open();

            DataTable datatable = new DataTable();

            SqlDataAdapter sqlDataAdapter = new SqlDataAdapter(cmd);
            sqlDataAdapter.Fill(datatable);
            con.Close();
            sqlDataAdapter.Dispose();

            List<int> devIDs = new List<int>();


            for (int a = 0; a < datatable.Rows.Count; a++)
                devIDs.Add(Convert.ToInt32(datatable.Rows[a]["delivery_id"]));

            return devIDs;
        }

        public List<int> getSaleIDs(int SiteID, int s_SaleID, int e_SaleID)
        {
            using SqlConnection con = new SqlConnection();
            SqlCommand cmd = new SqlCommand("Select Sale_ID from Fintech_Transactions where Site_ID = @Omni_siteid and sale_id >= @s_saleid and sale_id <= @e_saleid", con);
            cmd.Parameters.AddWithValue("@Omni_siteid", SiteID);
            cmd.Parameters.AddWithValue("@s_saleid", s_SaleID);
            cmd.Parameters.AddWithValue("@e_saleid", e_SaleID);
            con.ConnectionString = (connectToDb);


            cmd.CommandTimeout = 180;
            con.Open();

            DataTable datatable = new DataTable();

            SqlDataAdapter sqlDataAdapter = new SqlDataAdapter(cmd);
            sqlDataAdapter.Fill(datatable);
            con.Close();
            sqlDataAdapter.Dispose();

            List<int> saleIDs = new List<int>();


            for (int a = 0; a < datatable.Rows.Count; a++)
                saleIDs.Add(Convert.ToInt32(datatable.Rows[a]["Sale_ID"]));

            return saleIDs;
        }

        public List<int> getexistingTankStatus(int OmniSiteID, DateTime timeStamp, OmniFintechMapping map)
        {
            //List<int> tankIDs = map.getTanks(OmniSiteID);

            using SqlConnection con = new SqlConnection();
            SqlCommand cmd = new SqlCommand("Select tank_id from Fintech_TankLevels where Site_ID = @Omni_siteid and FC_TimeStamp = @timestamp", con);
            cmd.Parameters.AddWithValue("@Omni_siteid", OmniSiteID);
            cmd.Parameters.AddWithValue("@timestamp", timeStamp);
            con.ConnectionString = (connectToDb);

            cmd.CommandTimeout = 180;
            con.Open();

            DataTable datatable = new DataTable();

            SqlDataAdapter sqlDataAdapter = new SqlDataAdapter(cmd);
            sqlDataAdapter.Fill(datatable);
            con.Close();
            sqlDataAdapter.Dispose();

            List<int> tankIDs = new List<int>();

            for (int a = 0; a < datatable.Rows.Count; a++)
                tankIDs.Add(Convert.ToInt32(datatable.Rows[a]["tank_id"]));

            return tankIDs;
        }



        public void AddDataToTransactions(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            int FintechSiteId;
            double OmniSiteId;


            Console.WriteLine("Adding to Transactions");
            logger.Info("Adding to Transactions");





            Console.WriteLine("columnName :" + dt.Columns[0].ColumnName);
            Console.WriteLine("Site ID - Row 0:" + dt.Rows[0][0]);

            FintechSiteId = Convert.ToInt32(dt.Rows[0][0]);
            logger.Info("get Omni Site ID against FC_SiteID");
            OmniSiteId = map.getOmniSiteID(FintechSiteId);

            Console.WriteLine("OmniSiteID :" + OmniSiteId);
            logger.Info("modifying transactions data table");
            /* add new columns to the data table */
            dt.Columns.Add("Article", typeof(string));
            dt.Columns.Add("OmniSite_ID", typeof(Int32));
            dt.Columns.Add("FC_StartDateTime", typeof(DateTime));
            dt.Columns.Add("FC_EndDateTime", typeof(DateTime));
            dt.Columns.Add("createdon", typeof(DateTime));


            DataTable transactions = dt.Clone();
            transactions.Columns[2].DataType = typeof(System.String);
            transactions.Columns[3].DataType = typeof(System.String);
            transactions.Columns[14].DataType = typeof(System.String);
            transactions.Columns[15].DataType = typeof(System.String);

            logger.Info("getting sale ids");
            List<int> saleids = getSaleIDs(Convert.ToInt32(OmniSiteId), Convert.ToInt32(dt.Rows[0][1]), Convert.ToInt32(dt.Rows[dt.Rows.Count - 1][1]));
            int SaleId;
            DateTime FC_StartDateTime;
            DateTime FC_EndDateTime;
            string Article;

            //Console.WriteLine("Transforming Columns in each Row\n");
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                var dataRow = dt.Rows[i];
                try
                {
                    //logger.Info("\nAddDataToTransactions -> getOmniSiteID\n");
                    SaleId = Convert.ToInt32(dataRow[1]);
                    dataRow[2] = Convert.ToString(dt.Rows[i][2]);
                    dataRow[3] = Convert.ToString(dt.Rows[i][3]);
                    dataRow[14] = Convert.ToString(dt.Rows[i][14]);
                    dataRow[15] = Convert.ToString(dt.Rows[i][15]);
                }
                catch (Exception e)
                {
                    if (e is SqlException) throw;
                    //logger.writelog(e.Message.ToString());
                    logger.Error("DB-AddDataToTransactions -" + e.Message, e);
                    Console.WriteLine(e);
                    throw;
                }

                //Combining the dates
                FC_StartDateTime = getConvertedDateTime(Convert.ToString(dataRow[14]), Convert.ToString(dataRow[15]));
                FC_EndDateTime = getConvertedDateTime(Convert.ToString(dataRow[2]), Convert.ToString(dataRow[3]));

                //Getting ArticleName

                Article = map.getArticleName(Convert.ToInt32(OmniSiteId), Convert.ToInt32(dataRow[6]));

                if (Article == null)
                {
                    logger.Warn($"Product mapping not found for SiteID={OmniSiteId}, ProductID={dataRow[6]}. Skipping row with SaleId={SaleId}.");
                    continue;
                }

                if (!saleids.Contains(SaleId))
                //if (!alreadyTransExists(dataRow, OmniSiteId))
                {
                    dataRow["OmniSite_ID"] = OmniSiteId;
                    dataRow["Article"] = Article;
                    dataRow["FC_StartDateTime"] = FC_StartDateTime;
                    dataRow["FC_EndDateTime"] = FC_EndDateTime;
                    dataRow["createdon"] = DateTime.Now.AddHours(5);
                    transactions.Rows.Add(dataRow.ItemArray);


                }
                else
                {
                    Console.WriteLine("Sale ID " + Convert.ToString(SaleId) + " already exists. Duplicate");
                    logger.Warn("Duplicate Transaction Row Found for Sale Id" + SaleId);

                    var line = $"{OmniSiteId},{dataRow[1]},{dataRow[2]},{dataRow[3]},{dataRow[4]},{dataRow[5]},{dataRow[6]}," +
                        $"{dataRow[7]},{dataRow[8]},{dataRow[9]},{dataRow[10]},{dataRow[11]},{dataRow[12]},{dataRow[13]},{dataRow[14]},{dataRow[15]}," +
                        $"{dataRow[16]},{dataRow[17]},{dataRow[18]},{DateTime.Now.AddHours(5)},{""},{""},{""},{Article},{""},{FintechSiteId},{FC_StartDateTime},{FC_EndDateTime}";

                    WriteDuplicateToCSV("duplicateTransactions.csv", line, "Transaction");

                    //dt.Rows.Remove(dt.Rows[i]);INSE

                }
                //Console.WriteLine(dt.Rows[i].ItemArray);
                // dataRow = dt.Rows[i];

            }

            if (transactions.Rows.Count > 0)
            {
                logger.Info("Starting Bulk Insert for Transactions");
                insertBulkTransactions(transactions, processedFileId);
                logger.Info("Bulk Insert for Transactions completed");
            }



        }


        public void insertBulkTransactions(DataTable dt, int? processedFileId)
        {
            Console.WriteLine("Inserting into transactions bulk\n");
            if (dt == null)
                return;
            dt.Columns["OmniSite_ID"].ColumnName = "Site_ID";
            dt.Columns["site_id"].ColumnName = "FC_SiteID";
            dt.Columns["Site_ID"].SetOrdinal(0);
            dt.Columns["FC_SiteID"].SetOrdinal(20);
            if (dt.Rows.Count > 0)
            {
                Console.WriteLine("Inserting Data in Fintech Transactions (Bulk) \n");
                
                try
                {
                    // Insert to primary database
                    using (SqlConnection con = new SqlConnection(connectToDb))
                        {
                            con.Open();
                            SqlCommand cmd = new SqlCommand("InsertintoFintechTransactions", con);
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.Parameters.Add("@TransactionsTable", SqlDbType.Structured).Value = dt;
                            cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                            cmd.ExecuteNonQuery();
                            logger.Info("Primary DB - Transactions inserted successfully");
                        }
                    
                    // Insert to secondary database if enabled
                    if (enableDualInsert && !string.IsNullOrEmpty(secondaryDbConnectionString))
                    {
                        using (SqlConnection con = new SqlConnection(secondaryDbConnectionString))
                            {
                                con.Open();
                                SqlCommand cmd = new SqlCommand("InsertintoFintechTransactions", con);
                                cmd.CommandType = CommandType.StoredProcedure;
                                cmd.Parameters.Add("@TransactionsTable", SqlDbType.Structured).Value = dt;
                                cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                                cmd.ExecuteNonQuery();
                                logger.Info("Secondary DB - Transactions inserted successfully");
                            }
                    }
                }
                catch (Exception ex)
                {
                    if (ex is SqlException) throw;
                    logger.Error("insertBulkTransactions - " + ex.Message, ex);
                    FtpConnectivity.isExceptionOccured = true;
                    throw;
                }
            }
        }



        public List<int> TransacsgetSaleIDs(int SiteID, int s_SaleID, int e_SaleID)
        {

            Console.WriteLine("get transactions sale ids\n");

            using SqlConnection con = new SqlConnection();
            SqlCommand cmd = new SqlCommand("Select Sale_ID from Fintech_transactionsType where Site_ID = @Omni_siteid and sale_id >= @s_saleid and sale_id <= @e_saleid", con);
            cmd.Parameters.AddWithValue("@Omni_siteid", SiteID);
            cmd.Parameters.AddWithValue("@s_saleid", s_SaleID);
            cmd.Parameters.AddWithValue("@e_saleid", e_SaleID);
            con.ConnectionString = (connectToDb);

            cmd.CommandTimeout = 180;

            con.Open();

            DataTable datatable = new DataTable();

            SqlDataAdapter sqlDataAdapter = new SqlDataAdapter(cmd);
            sqlDataAdapter.Fill(datatable);
            con.Close();
            sqlDataAdapter.Dispose();

            List<int> saleIDs = new List<int>();


            for (int a = 0; a < datatable.Rows.Count; a++)
                saleIDs.Add(Convert.ToInt32(datatable.Rows[a]["Sale_ID"]));

            return saleIDs;
        }

        public void AddDataToTransactionsType(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            int FintechSiteId;
            double OmniSiteId;


            FintechSiteId = Convert.ToInt32(dt.Rows[0][0]);
            OmniSiteId = map.getOmniSiteID(FintechSiteId);

            dt.Columns.Add("OmniSite_ID", typeof(Int32));
            dt.Columns.Add("CreatedOn", typeof(DateTime));
            dt.Columns.Add("Modified_Time", typeof(DateTime));

            DataTable transactions = dt.Clone();

            if (dt.Rows.Count > 0)
            {
                //convert DataTable to DataView
                DataView dv = dt.DefaultView;
                //apply the sort on CustomerSurname column
                dv.Sort = "pay_sale_id";
                //save our newly ordered results back into our datatable
                dt = dv.ToTable();
            }

            List<int> SaleIDs = TransacsgetSaleIDs(Convert.ToInt32(OmniSiteId), Convert.ToInt32(dt.Rows[0][1]), Convert.ToInt32(dt.Rows[dt.Rows.Count - 1][1]));


            try
            {
                if (dt.Rows.Count > 0)
                {

                    for (int i = 0; i < dt.Rows.Count; i++)
                    {

                        var DataRow = dt.Rows[i];
                        if (!SaleIDs.Contains(Convert.ToInt32(DataRow[1])))
                        {

                            DataRow["OmniSite_ID"] = OmniSiteId;

                            DataRow["CreatedOn"] = DateTime.Now.AddHours(5);

                            DataRow["Modified_Time"] = DateTime.ParseExact(Convert.ToString(DataRow["last_modified_date"]), "yyyyMMddHHmmss", new CultureInfo("en-US"));

                            transactions.Rows.Add(DataRow.ItemArray);
                        }
                        else
                        {
                            Console.WriteLine("Sale ID " + Convert.ToString(Convert.ToInt32(DataRow[1])) + " already exists in TransactionsType. Duplicate");
                            logger.Warn("Duplicate Transaction Row Found in PPD TransactionsType for SaleId " + Convert.ToString(Convert.ToInt32(DataRow[1])));

                            var line = $"{OmniSiteId},{DataRow[1]},{DataRow[2]},{DataRow[3]},{DataRow[4]},{DateTime.ParseExact(Convert.ToString(DataRow["last_modified_date"]), "yyyyMMddHHmmss", new CultureInfo("en-US"))},{DataRow[0]},{DateTime.Now.AddHours(5)}";

                            WriteDuplicateToCSV("duplicateTransactionsType.csv", line, "TransactionType");

                        }

                    }

                    Console.WriteLine("Adding Data to Fintech_Transactions_Type (Bulk) \n");

                    try
                    {
                        // Insert to primary database
                        using (SqlConnection con = new SqlConnection(connectToDb))
                        {
                            con.Open();
                            SqlCommand cmd = new SqlCommand("InsertintoFintechTransactionsType", con);
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.Parameters.Add("@UpdatedTransactions", SqlDbType.Structured).Value = transactions;
                            cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                            cmd.ExecuteNonQuery();
                            logger.Info("Primary DB - TransactionsType inserted successfully");
                        }
                        
                        // Insert to secondary database if enabled
                        if (enableDualInsert && !string.IsNullOrEmpty(secondaryDbConnectionString))
                        {
                            using (SqlConnection con = new SqlConnection(secondaryDbConnectionString))
                            {
                                con.Open();
                                SqlCommand cmd = new SqlCommand("InsertintoFintechTransactionsType", con);
                                cmd.CommandType = CommandType.StoredProcedure;
                                cmd.Parameters.Add("@UpdatedTransactions", SqlDbType.Structured).Value = transactions;
                                cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                                cmd.ExecuteNonQuery();
                                logger.Info("Secondary DB - TransactionsType inserted successfully");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                    if (ex is SqlException) throw;
                        logger.Error("AddDataToTransactionsType - " + ex.Message, ex);
                        FtpConnectivity.isExceptionOccured = true;
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                    if (ex is SqlException) throw;
                logger.Error("AddDataToTransactionsType - " + ex.Message, ex);
                throw;
            }

            /*
            dt.Columns.Add("OmniSite_ID", typeof(Int32));

            
            DataTable transactions = dt.Clone();


            for (int i = 0; i < dt.Rows.Count; i++)
            {
                logger.writelog("reading row " + i.ToString() + " of the table\n");
                var dataRow = dt.Rows[i];
                dataRow["OmniSite_ID"] = OmniSiteId;

                transactions.Rows.Add(dataRow.ItemArray);


            }*/



        }

        public void AddDataToTanks(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            int FintechSiteId;
            double OmniSiteId;
            int FintechTankId;
            double OmniAssetID;
            FintechSiteId = Convert.ToInt32(dt.Rows[0][0]);
            OmniSiteId = map.getOmniSiteID(FintechSiteId);

            DateTime timeStamp = Convert.ToDateTime(dt.Rows[0][dt.Columns.Count - 1]);


            List<int> tankIDs = getexistingTankStatus(Convert.ToInt32(OmniSiteId), timeStamp, map);


            /* add new columns to the data table */
            dt.Columns.Add("CreatedOn", typeof(DateTime));
            dt.Columns.Add("FC_SiteID", typeof(int));
            dt.Columns.Add("Omni_AssetID", typeof(int));
            dt.Columns.Add("FC_TimeStamp", typeof(DateTime));

            DataTable tankStatus = dt.Clone();

            for (int i = 0; i < dt.Rows.Count; i++)
            {
                var dataRow = dt.Rows[i];

                try
                {
                    FintechTankId = Convert.ToInt32(dataRow[1]);
                    OmniAssetID = map.getOmniAssetID(FintechSiteId, FintechTankId);
                }
                catch (Exception ex)
                {
                    if (ex is SqlException) throw;
                    logger.Error("AddDataToTanks- " + ex.Message, ex);
                    throw;
                }
                //Check if record against tank is present then update else insert new to TankStatusLive Table
                //insertUpdateTanksDataToTankStatusLiveTb(dataRow, OmniSiteId, FintechSiteId, FintechTankId, OmniAssetID);
                ////
                //Inserting data to Tank Status historical

                if (!tankIDs.Contains(FintechTankId))
                {
                    dataRow["CreatedOn"] = DateTime.Now.AddHours(5);
                    dataRow["FC_TimeStamp"] = timeStamp;
                    dataRow["Omni_AssetID"] = map.getOmniAssetID(FintechSiteId, FintechTankId);
                    dataRow["FC_SiteID"] = FintechSiteId;
                    dataRow["Site_ID"] = OmniSiteId;
                    tankStatus.Rows.Add(dataRow.ItemArray);
                }

            }

            insertBulkTankStatus(tankStatus, processedFileId);
        }


        public void insertBulkTankStatus(DataTable dt, int? processedFileId)
        {
            if (dt == null)
                return;
            if (dt.Rows.Count > 0)
            {
                Console.WriteLine("Inserting Data in FintechTankLevels (Bulk) \n");
                
                try
                {
                    // Insert to primary database
                    using (SqlConnection con = new SqlConnection(connectToDb))
                        {
                            con.Open();
                            SqlCommand cmd = new SqlCommand("InsertintoFintechTankLevels", con);
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.Parameters.Add("@TankStatusTable", SqlDbType.Structured).Value = dt;
                            cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                            cmd.ExecuteNonQuery();
                            logger.Info("Primary DB - Tank Status inserted successfully");
                        }
                    
                    // Insert to secondary database if enabled
                    if (enableDualInsert && !string.IsNullOrEmpty(secondaryDbConnectionString))
                    {
                        using (SqlConnection con = new SqlConnection(secondaryDbConnectionString))
                            {
                                con.Open();
                                SqlCommand cmd = new SqlCommand("InsertintoFintechTankLevels", con);
                                cmd.CommandType = CommandType.StoredProcedure;
                                cmd.Parameters.Add("@TankStatusTable", SqlDbType.Structured).Value = dt;
                                cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                                cmd.ExecuteNonQuery();
                                logger.Info("Secondary DB - Tank Status inserted successfully");
                            }
                    }
                }
                catch (Exception ex)
                {
                    if (ex is SqlException) throw;
                    logger.Error("insertBulkTankStatus- " + ex.Message, ex);
                    FtpConnectivity.isExceptionOccured = true;
                    throw;
                }
            }
        }


        public void AddDataToTDD(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {

            int FintechSiteId;
            double OmniSiteId;
            FintechSiteId = Convert.ToInt32(dt.Rows[0][0]);
            OmniSiteId = map.getOmniSiteID(FintechSiteId);
            double omniassetid;
            int tankId, deliveryID, productID;

            DataTable delivery = dt.Clone();


            List<int> deliveryids = getDeliveryIDs(Convert.ToInt32(OmniSiteId), Convert.ToInt32(dt.Rows[0][18]), Convert.ToInt32(dt.Rows[dt.Rows.Count - 1][18]));

            for (int i = 0; i < dt.Rows.Count; i++)
            {
                var dataRow = dt.Rows[i];
                try
                {
                    deliveryID = Convert.ToInt32(dataRow[18]);
                    tankId = Convert.ToInt32(dataRow[1]);
                    omniassetid = map.getOmniAssetID(FintechSiteId, tankId);
                    productID = map.getTank_ProductID(Convert.ToInt32(omniassetid));

                }
                catch (Exception e)
                {
                    if (e is SqlException) throw;
                    logger.Error("AddDataToTDD - " + e.Message, e);
                    throw;
                }

                //Combining the dates;
                DateTime FC_EndDateTime = getConvertedDateTime(Convert.ToString(dataRow[5]), Convert.ToString(dataRow[6]));
                if (!deliveryids.Contains(deliveryID))
                //if (!isAlreadExistingDelivery(deliveryID,Convert.ToInt32(OmniSiteId),Convert.ToInt32(omniassetid)))
                {
                    try
                    {
                        // Insert to primary database
                        using (SqlConnection sc = new SqlConnection(connectToDb))
                        {
                            SqlCommand cmd = new SqlCommand("InsertintoFintechTanksDelivery", sc);
                            cmd.CommandType = CommandType.StoredProcedure;
                            sc.Open();
                            addDeliveryProcParam(ref cmd, dataRow, FintechSiteId, tankId, FC_EndDateTime, omniassetid, OmniSiteId, productID, processedFileId);
                            cmd.ExecuteNonQuery();
                            sc.Close();
                            Console.WriteLine("Primary DB - Delivery at " + FC_EndDateTime.ToString() + " for Site " + FintechSiteId + " added.");
                        }
                        
                        // Insert to secondary database if enabled
                        if (enableDualInsert && !string.IsNullOrEmpty(secondaryDbConnectionString))
                        {
                            using (SqlConnection sc = new SqlConnection(secondaryDbConnectionString))
                            {
                                SqlCommand cmd = new SqlCommand("InsertintoFintechTanksDelivery", sc);
                                cmd.CommandType = CommandType.StoredProcedure;
                                sc.Open();
                                addDeliveryProcParam(ref cmd, dataRow, FintechSiteId, tankId, FC_EndDateTime, omniassetid, OmniSiteId, productID, processedFileId);
                                cmd.ExecuteNonQuery();
                                sc.Close();
                                Console.WriteLine("Secondary DB - Delivery at " + FC_EndDateTime.ToString() + " for Site " + FintechSiteId + " added.");
                            }
                        }
                        
                        delivery.Rows.Add(dataRow.ItemArray);
                    }
                    catch (Exception ex)
                    {
                    if (ex is SqlException) throw;
                        logger.Error("AddDataToTDD - " + ex.Message, ex);
                        FtpConnectivity.isExceptionOccured = true;
                        throw;
                    }



                }
                else
                {
                    var line = $"{OmniSiteId},{tankId},{getConvertedDate(dataRow[2].ToString())},{getConvertedTime(dataRow[3].ToString())}" +
                        $",{dataRow[4]},{getConvertedDate(dataRow[5].ToString())},{getConvertedTime(dataRow[6].ToString())},{dataRow[7]}," +
                        $"{dataRow[8]},{dataRow[9]},{dataRow[10]},{dataRow[11]},{dataRow[12]},{dataRow[13]},{dataRow[14]},{dataRow[15]},{dataRow[16]}," +
                        $"{dataRow[17]},{dataRow[18]},{dataRow[19]},{dataRow[20]},{dataRow[21]},{dataRow[22]},{dataRow[24]},{dataRow[25]}," +
                        $"{dataRow[26]},{dataRow[28]},{dataRow[27]},{FC_EndDateTime},{FintechSiteId},{omniassetid},{DateTime.Now.ToString()}";

                    WriteDuplicateToCSV("duplicateDeliveries.csv", line, "Delivery");

                }
            }

        }
        public HashSet<string> GetExistingAlarmKeys(int siteId, DateTime minDate, DateTime maxDate)
        {
            var keys = new HashSet<string>();
            using (var con = new SqlConnection(connectToDb))
            using (var cmd = new SqlCommand(@"
            SELECT 
                CONCAT(Site_ID, '|', Alarm_Type, '|', Location_Type, '|', Location_ID, '|', FORMAT(fc_alarmdatetime, 'yyyyMMddHHmmss')) AS AlarmKey
            FROM Fintech_Alarms
            WHERE Site_ID = @SiteId
              AND fc_alarmdatetime BETWEEN @MinDate AND @MaxDate;", con))
            {
                cmd.Parameters.AddWithValue("@SiteId", siteId);
                cmd.Parameters.AddWithValue("@MinDate", minDate);
                cmd.Parameters.AddWithValue("@MaxDate", maxDate);

                con.Open();
                cmd.CommandTimeout = 180;
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                        keys.Add(rdr["AlarmKey"].ToString());
            }
            return keys;
        }


        public void AddDataToAlarms(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            int fintechSiteId = Convert.ToInt32(dt.Rows[0]["site_id"]);
            double omniSiteId = map.getOmniSiteID(fintechSiteId);

            // Define target DataTable structure same as TVP
            DataTable alarms = new DataTable();
            alarms.Columns.Add("Site_ID", typeof(int));
            alarms.Columns.Add("Alarm_Type", typeof(string));
            alarms.Columns.Add("Location_Type", typeof(string));
            alarms.Columns.Add("Location_ID", typeof(int));
            alarms.Columns.Add("Alarm_Status", typeof(string));
            alarms.Columns.Add("Alarm_Date", typeof(DateTime));
            alarms.Columns.Add("Alarm_Time", typeof(TimeSpan));
            alarms.Columns.Add("Last_Modified_Date", typeof(DateTime));
            alarms.Columns.Add("Severity", typeof(string));
            alarms.Columns.Add("Ack_User", typeof(string));
            alarms.Columns.Add("Ack_Date", typeof(DateTime));
            alarms.Columns.Add("Ack_Time", typeof(TimeSpan));
            alarms.Columns.Add("Auto_Reset", typeof(bool));
            alarms.Columns.Add("CreatedOn", typeof(DateTime));
            alarms.Columns.Add("fc_alarmdatetime", typeof(DateTime));

            try
            {
                // Combine alarm_date + alarm_time into one DateTime for range bounds
                DateTime minDate = dt.AsEnumerable()
                    .Select(r =>
                    {
                        string dateStr = r["alarm_date"].ToString().Trim();
                        string timeStr = r["alarm_time"].ToString().Trim();

                        // Ensure time has 6 digits (HHmmss)
                        timeStr = timeStr.PadLeft(6, '0');

                        return DateTime.ParseExact(
                            dateStr + timeStr,
                            "yyyyMMddHHmmss",
                            CultureInfo.InvariantCulture);
                    })
                    .Min();

                DateTime maxDate = dt.AsEnumerable()
                    .Select(r =>
                    {
                        string dateStr = r["alarm_date"].ToString().Trim();
                        string timeStr = r["alarm_time"].ToString().Trim();

                        // Ensure time has 6 digits (HHmmss)
                        timeStr = timeStr.PadLeft(6, '0');

                        return DateTime.ParseExact(
                            dateStr + timeStr,
                            "yyyyMMddHHmmss",
                            CultureInfo.InvariantCulture);
                    })
                    .Max();

                // Get existing alarm keys from DB
                HashSet<string> existingKeys = GetExistingAlarmKeys((int)omniSiteId, minDate, maxDate);


                foreach (DataRow row in dt.Rows)
                {
                    // Build combined datetime
                    DateTime? fcAlarmDateTime = null;
                    string dateStr = row["alarm_date"].ToString().Trim();
                    string timeStr = row["alarm_time"].ToString().Trim().PadLeft(6, '0');
                    if (DateTime.TryParseExact(dateStr + timeStr,
                                               "yyyyMMddHHmmss",
                                               CultureInfo.InvariantCulture,
                                               DateTimeStyles.None,
                                               out var parsedFcDt))
                    {
                        fcAlarmDateTime = parsedFcDt;
                    }
                    // Use fcAlarmDateTime (formatted consistently with SQL)
                    string alarmKey = $"{omniSiteId}|{row["alarm_type"]}|{row["location_type"]}|{row["location_id"]}|{fcAlarmDateTime:yyyyMMddHHmmss}";


                    if (!existingKeys.Contains(alarmKey))
                    {
                        // Parse alarm date/time safely
                        DateTime alarmDate = DateTime.TryParseExact(row["alarm_date"].ToString(), "yyyyMMdd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                            ? parsedDate : DateTime.MinValue;

                        // Pad alarm_time to 6 digits before parsing
                        string alarmTimeStr = row["alarm_time"].ToString().Trim().PadLeft(6, '0');
                        TimeSpan alarmTime = TimeSpan.TryParseExact(alarmTimeStr, "hhmmss",
                            CultureInfo.InvariantCulture, out var parsedTime)
                            ? parsedTime : TimeSpan.Zero;

                        DateTime lastModified = DateTime.TryParseExact(row["last_modified_date"].ToString(), "yyyyMMddHHmmss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedLast)
                            ? parsedLast : DateTime.Now;

                        DateTime? ackDate = DateTime.TryParseExact(row["ack_date"].ToString(), "yyyyMMdd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedAckDate)
                            ? parsedAckDate : (DateTime?)null;

                        // Pad ack_time to 6 digits before parsing
                        string ackTimeStr = row["ack_time"].ToString().Trim().PadLeft(6, '0');
                        TimeSpan? ackTime = TimeSpan.TryParseExact(ackTimeStr, "hhmmss",
                            CultureInfo.InvariantCulture, out var parsedAckTime)
                            ? parsedAckTime : (TimeSpan?)null;

                        bool? autoReset = null;
                        string autoResetRaw = row["auto_reset"].ToString().Trim();
                        if (autoResetRaw == "1") autoReset = true;
                        else if (autoResetRaw == "0") autoReset = false;

                        

                        alarms.Rows.Add(
                            omniSiteId,
                            row["alarm_type"].ToString(),
                            row["location_type"].ToString(),
                            string.IsNullOrWhiteSpace(row["location_id"].ToString()) ? (object)DBNull.Value : Convert.ToInt32(row["location_id"]),
                            row["alarm_status"].ToString(),
                            alarmDate == DateTime.MinValue ? (object)DBNull.Value : alarmDate,
                            alarmTime == TimeSpan.Zero ? (object)DBNull.Value : alarmTime,
                            lastModified,
                            row["severity"].ToString(),
                            row["ack_user"].ToString(),
                            ackDate.HasValue ? (object)ackDate.Value : DBNull.Value,
                            ackTime.HasValue ? (object)ackTime.Value : DBNull.Value,
                            autoReset.HasValue ? (object)autoReset.Value : DBNull.Value,
                            DateTime.Now.AddHours(5),
                            fcAlarmDateTime.HasValue ? (object)fcAlarmDateTime.Value : DBNull.Value
                            );
                    }
                    else
                    {
                        Console.WriteLine($"Duplicate alarm detected: {alarmKey}");
                        logger.Warn($"Duplicate Alarm Row Found for Key: {alarmKey}");

                        // Write duplicate row into CSV
                        var line = $"{omniSiteId},{row["alarm_type"]},{row["location_type"]},{row["location_id"]},{row["alarm_status"]}," +
                                   $"{row["alarm_date"]},{row["alarm_time"]},{row["last_modified_date"]},{row["severity"]}," +
                                   $"{row["ack_user"]},{row["ack_date"]},{row["ack_time"]},{row["auto_reset"]},{DateTime.Now.AddHours(5)}";

                        WriteDuplicateToCSV("duplicateAlarms.csv", line, "Alarm");
                    }
                }

                if (alarms.Rows.Count > 0)
                {
                    Console.WriteLine("Adding Data to Fintech_Alarms (Bulk) \n");

                    try
                    {
                        // Insert to primary database
                        using (var con = new SqlConnection(connectToDb))
                        {
                            con.Open();
                            SqlCommand cmd = new SqlCommand("InsertintoFintechAlarms", con);
                            cmd.CommandType = CommandType.StoredProcedure;
                            cmd.Parameters.Add("@AlarmsTable", SqlDbType.Structured).Value = alarms;
                            cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                            cmd.ExecuteNonQuery();
                            logger.Info("Primary DB - Alarms inserted successfully");
                        }
                        
                        // Insert to secondary database if enabled
                        if (enableDualInsert && !string.IsNullOrEmpty(secondaryDbConnectionString))
                        {
                            using (var con = new SqlConnection(secondaryDbConnectionString))
                            {
                                con.Open();
                                SqlCommand cmd = new SqlCommand("InsertintoFintechAlarms", con);
                                cmd.CommandType = CommandType.StoredProcedure;
                                cmd.Parameters.Add("@AlarmsTable", SqlDbType.Structured).Value = alarms;
                                cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;
                                cmd.ExecuteNonQuery();
                                logger.Info("Secondary DB - Alarms inserted successfully");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                    if (ex is SqlException) throw;
                        logger.Error("AddAlarms - " + ex.Message, ex);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                    if (ex is SqlException) throw;
                logger.Error("AddDataToAlarms - " + ex.Message, ex);
                FtpConnectivity.isExceptionOccured = true;
            }
        }






        public string generateTransactionsJSON(DataRow dt, double OmniSiteID, int FintechSiteID, DateTime FC_StartDateTimE, DateTime FC_EndDateTimE, string Article)
        {
            logger.Info("Creating Transaction JSON.");
            //creating a JSON
            var transactions = new
            {
                ID = 1,
                Site_ID = Convert.ToInt32(OmniSiteID),
                Sale_ID = Convert.ToInt32(dt[1]),
                End_Date = Convert.ToString(dt[2]),
                End_Time = Convert.ToString(dt[3]),
                Pump_ID = Convert.ToInt32(dt[4]),
                Hose_ID = Convert.ToInt32(dt[5]),
                Grade_ID = Convert.ToInt32(dt[6]),
                Volume = Convert.ToDouble(dt[7]),
                Money = Convert.ToDouble(dt[8]),
                PPU = Convert.ToDouble(dt[9]),
                Level = Convert.ToInt32(dt[10]),
                Sale_Type = Convert.ToInt32(dt[11]),
                Initial_Volume = Convert.ToDouble(dt[12]),
                Final_Volume = Convert.ToDouble(dt[13]),
                Start_Date = Convert.ToString(dt[14]),
                Start_Time = Convert.ToString(dt[15]),
                Preset_Amount = Convert.ToDouble(dt[16]),
                Sale_Auth = dt[17],
                Authorizer = dt[18],
                CreatedOn = DateTime.Now.AddHours(5),
                Article = Article,
                FC_SiteID = FintechSiteID,
                FC_StartDateTime = FC_StartDateTimE,
                FC_EndDateTime = FC_EndDateTimE

            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(transactions);
        }


        public string generateTankJSON(DataRow dt)
        {
            logger.Info("Creating TankStatus JSON.");
            var tankstatus = new
            {
                ID = 1,
                Site_ID = dt[0],
                Tank_ID = Convert.ToInt32(dt[1]),
                Product_ID = dt[2],
                Product_Number = dt[3],
                Product_Volume = dt[4],
                Product_Height = dt[5],
                Water_Volume = dt[6],
                Water_Height = dt[7],
                Product_Temperature = dt[8],
                Date_Last_Read = Convert.ToString(dt[9]),
                Time_Last_Read = Convert.ToString(dt[10]),
                Last_Trans_ID = dt[11],
                Accum_Volume = dt[12],
                IsAccum = dt[13],
                Delivery_ini_Product_Volume = dt[14],
                Delivery_ini_Product_Height = dt[15],
                Delivery_ini_Water_Volume = dt[16],
                Delivery_ini_Water_Height = dt[17],
                Delivery_Start_Date = dt[18],
                Delivery_Start_Time = dt[19],
                Probe_Status = Convert.ToString(dt[20]),
                delivery_ini_temp = dt[21],
                TC_Volume = dt[22],
                Date_Last_Leak_Test = dt[23],
                Time_Last_Leak_Test = dt[24],
                Last_Modified_Date = Convert.ToString(dt[25]),
                Product_Density = dt[26],
                Product_Weight = dt[27],
                Ullage = dt[28],
                Delivered_Volume = dt[29],
                Delivery_State = dt[30],
                prod_tc_density = dt[31],
                Time_Stamp = dt[32],
                CreatedOn = DateTime.Now.AddHours(5),
                FC_SiteID = dt[34],
                Omni_AssetID = dt[35],
                FC_TimeStamp = dt[36]

            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(tankstatus);
        }


        // Coerce a CSV cell to a decimal SqlParameter value; blank or non-numeric input becomes DBNull
        // so SqlClient's parameter coercion (Convert.ToDecimal) doesn't throw FormatException.
        private static object ToDecimalParam(object v)
        {
            if (v == null || v == DBNull.Value) return DBNull.Value;
            var s = v.ToString().Trim();
            if (s.Length == 0) return DBNull.Value;
            return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                   ? (object)d : DBNull.Value;
        }

        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        public void addDeliveryProcParam(ref SqlCommand cmd, DataRow dt, int FC_SiteID, int tankID, DateTime last_modified_date, double OmniAssetID, double OmniSiteID, int ProductID, int? processedFileId)
        {
            cmd.Parameters.Add("@Omni_SiteID", SqlDbType.Int).Value = OmniSiteID;
            cmd.Parameters.Add("@tank_id", SqlDbType.Int).Value = tankID;
            cmd.Parameters.Add("@date_start", SqlDbType.Date).Value = getConvertedDate(dt[2].ToString());
            cmd.Parameters.Add("@time_start", SqlDbType.Time).Value = getConvertedTime(dt[3].ToString());
            cmd.Parameters.Add("@vol_total", SqlDbType.Decimal).Value = ToDecimalParam(dt[4]);
            cmd.Parameters.Add("@date_end", SqlDbType.Date).Value = getConvertedDate(dt[5].ToString());
            cmd.Parameters.Add("@time_end", SqlDbType.Time).Value = getConvertedTime(dt[6].ToString());
            cmd.Parameters.Add("@vol_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[7]);
            cmd.Parameters.Add("@vol_end", SqlDbType.Decimal).Value = ToDecimalParam(dt[8]);
            cmd.Parameters.Add("@height_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[9]);
            cmd.Parameters.Add("@height_end", SqlDbType.Decimal).Value = ToDecimalParam(dt[10]);
            cmd.Parameters.Add("@temp_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[11]);
            cmd.Parameters.Add("@temp_end", SqlDbType.Decimal).Value = ToDecimalParam(dt[12]);
            cmd.Parameters.Add("@transactions_volume", SqlDbType.Decimal).Value = ToDecimalParam(dt[13]);
            cmd.Parameters.Add("@water_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[14]);
            cmd.Parameters.Add("@water_end", SqlDbType.Decimal).Value = ToDecimalParam(dt[15]);
            cmd.Parameters.Add("@water_height_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[16]);
            cmd.Parameters.Add("@water_height_end", SqlDbType.Decimal).Value = ToDecimalParam(dt[17]);
            cmd.Parameters.Add("@delivery_id", SqlDbType.Int).Value = dt[18];
            cmd.Parameters.Add("@delivery_note_id", SqlDbType.Int).Value = dt[19];
            cmd.Parameters.Add("@vol_tc_total", SqlDbType.Decimal).Value = ToDecimalParam(dt[20]);
            cmd.Parameters.Add("@vol_tc_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[21]);
            cmd.Parameters.Add("@vol_tc_end", SqlDbType.Decimal).Value = ToDecimalParam(dt[22]);
            cmd.Parameters.Add("@last_modified_date", SqlDbType.DateTime).Value = last_modified_date;
            cmd.Parameters.Add("@source_type", SqlDbType.VarChar, 40).Value = dt[24];
            cmd.Parameters.Add("@prod_density_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[25]);
            cmd.Parameters.Add("@prod_tc_density_start", SqlDbType.Decimal).Value = ToDecimalParam(dt[26]);
            cmd.Parameters.Add("@prod_tc_density_end", SqlDbType.Decimal).Value = ToDecimalParam(dt[28]);
            cmd.Parameters.Add("@prod_density_end", SqlDbType.VarChar, 40).Value = dt[27];
            cmd.Parameters.Add("@FCsite_id", SqlDbType.Int).Value = FC_SiteID;
            cmd.Parameters.Add("@Omni_AssetID", SqlDbType.Int).Value = OmniAssetID;
            cmd.Parameters.Add("@ProductID", SqlDbType.Int).Value = ProductID;
            cmd.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? (object)processedFileId.Value : DBNull.Value;

        }

        public string generateDeliveryJSON(DataRow dt, int FC_SiteID, int tankID, int product_ID, DateTime last_modified_date, int OmniAssetID, int OmniSiteID)
        {
            logger.Info("Creating Delivery JSON");
            var delivery = new
            {
                site_id = OmniSiteID,
                tank_id = tankID,
                start_datetime = getConvertedDateTime(dt[2].ToString(), dt[3].ToString()),
                vol_total = dt[4],
                end_datetime = getConvertedDateTime(dt[5].ToString(), dt[6].ToString()),
                vol_start = dt[7],
                vol_end = dt[8],
                height_start = dt[9],
                height_end = dt[10],
                temp_start = dt[11],
                temp_end = dt[12],
                transactions_volume = dt[13],
                water_start = dt[14],
                water_end = dt[15],
                water_height_start = dt[16],
                water_height_end = dt[17],
                delivery_id = dt[18],
                delivery_note_id = dt[19],
                vol_tc_total = dt[20],
                vol_tc_start = dt[21],
                vol_tc_end = dt[22],
                last_modified_date = last_modified_date,
                source_type = dt[24],
                prod_density_start = dt[25],
                prod_tc_density_start = dt[26],
                prod_density_end = dt[28],
                prod_tc_density_end = dt[27],
                ID = 1,
                FC_SiteID = FC_SiteID,
                Omni_AssetID = OmniAssetID,
                Product_ID = product_ID
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(delivery);

        }

        public string getConvertedTime(string actualTime)
        {
            actualTime = (actualTime.Length == 5) ? "0" + actualTime : (actualTime.Length == 4) ? "00" + actualTime : (actualTime.Length == 3) ? "000" + actualTime : (actualTime.Length == 2) ? "0000" + actualTime : (actualTime.Length == 1) ? "00000" + actualTime : (actualTime.Length == 0) ? "000000" + actualTime : actualTime;
            actualTime = actualTime.Substring(0, 2) + ":" + actualTime.Substring(2, 2) + ":" + actualTime.Substring(4, 2);
            return actualTime;
        }

        public string getConvertedDate(string actualDate)
        {
            actualDate = actualDate.Substring(0, 4) + "-" + actualDate.Substring(4, 2) + "-" + actualDate.Substring(6, 2);
            return actualDate;
        }

        public DateTime getConvertedDateTime(string actualDate, string actualTime)
        {
            actualTime = (actualTime.Length == 5) ? "0" + actualTime : (actualTime.Length == 4) ? "00" + actualTime : (actualTime.Length == 3) ? "000" + actualTime : (actualTime.Length == 2) ? "0000" + actualTime : (actualTime.Length == 1) ? "00000" + actualTime : (actualTime.Length == 0) ? "000000" + actualTime : actualTime;
            actualTime = actualTime.Substring(0, 2) + ":" + actualTime.Substring(2, 2) + ":" + actualTime.Substring(4, 2);
            actualDate = actualDate.Substring(0, 4) + "-" + actualDate.Substring(4, 2) + "-" + actualDate.Substring(6, 2);
            return DateTime.Parse(actualDate + " " + actualTime);
        }

        public void updateCountJSONS(int CustomerID, int JSON_TYPE, int count)
        {
            string sql_query = "update CountJSONS set JSONS_SENT += " + Convert.ToString(count) + " where Customer_id = " + Convert.ToString(CustomerID) + " and json_type = " + Convert.ToString(JSON_TYPE);
            Console.WriteLine(sql_query);
            using SqlConnection conn = new SqlConnection(connectToDb);
            SqlCommand cmd;
            conn.Open();
            cmd = new SqlCommand(sql_query, conn);
            cmd.ExecuteNonQuery();
            conn.Close();
        }

        public void callHourlyProcedureForRegeneratedFile(int siteId, DateTime startTime, DateTime endTime)
        {
            using (var con = new SqlConnection(connectToDb))
            {
                try
                {
                    //SqlConnection con = new SqlConnection(connectToDb);
                    SqlCommand cmd = new SqlCommand("Fintech_Regenerated_Files", con);
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@site_id", siteId);
                    cmd.Parameters.AddWithValue("@start_datetime", startTime);
                    cmd.Parameters.AddWithValue("@end_datetime", endTime);
                    con.Open();
                    cmd.ExecuteNonQuery();
                    // con.Close();
                }
                catch (Exception ex)
                {
                    if (ex is SqlException) throw;
                    logger.Error("callHourlyProcedureForRegeneratedFile- " + ex.Message, ex);
                    FtpConnectivity.isExceptionOccured = true;
                }
                finally { con.Close(); }

            }
        }

        public void AddDataToTanksForMagLink(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            try
            {
                Console.WriteLine($"Data reading completed, total rows found: {dt.Rows.Count}");
                insertBulkTankStatusForMagLink(dt, map, processedFileId);
            }
            catch (Exception ex)
            {
                    if (ex is SqlException) throw;
                throw;
            }
        }

        public void AddDataToTankDeliveryForMagLink(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            try
            {
                Console.WriteLine($"Data reading completed, total rows found: {dt.Rows.Count}");
                insertBulkTankDeliveryForMagLink(dt, map, processedFileId);
            }
            catch (Exception ex)
            {
                    if (ex is SqlException) throw;
                throw;
            }
        }


        public void insertBulkTankStatusForMagLink(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            if (dt == null)
                return;

            Console.WriteLine("Bulk insertion operation initiated...");
            Console.WriteLine($"Total records to process: {dt.Rows.Count}");

            List<FintechTankLevelsForMagLinkModel> list = new List<FintechTankLevelsForMagLinkModel>();
            StringBuilder builder = new StringBuilder();

            foreach (var row in dt.AsEnumerable().ToList())
            {
                var obj = new FintechTankLevelsForMagLinkModel()
                {
                    ibank = row["ibank"] != null ? Convert.ToInt32(row["ibank"]) : 0,
                    Tank = row["Tank"] != null ? Convert.ToInt32(row["Tank"]) : 0,
                    QTY = row["QTY"] != null ? float.Parse(row["QTY"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    Temperature = row["Temperature"] != null ? float.Parse(row["Temperature"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    Water = row["Water"] != null ? float.Parse(row["Water"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    Height = row["Height"] != null ? float.Parse(row["Height"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                };

                if (row["DateTime"] != null)
                    obj.DateTime = DateTime.Parse(row["DateTime"].ToString(), CultureInfo.InvariantCulture);

                obj.OmniSiteId = map.getOmniSiteID(obj.ibank);
                obj.OmniAssetId_Tank = map.getOmniAssetID(obj.ibank, obj.Tank);
                if (obj.OmniAssetId_Tank > 0)
                {
                    obj.ProductNumber = map.getTank_ProductID(obj.OmniAssetId_Tank);
                    obj.ProductId = map.getArticleName(obj.OmniSiteId, obj.ProductNumber);
                }

                list.Add(obj);
            }

            list.RemoveAll(l => l.DateTime.Year == 1970);
            WriteMagLinkRows(BuildMagLinkStockTable(list, processedFileId), "Fintech_TankLevels");
        }


        public void insertBulkTankDeliveryForMagLink(DataTable dt, OmniFintechMapping map, int? processedFileId)
        {
            if (dt == null)
                return;

            Console.WriteLine("Bulk insertion operation initiated...");
            Console.WriteLine($"Total records to process: {dt.Rows.Count}");

            List<FintechTankDeliveryForMagLinkModel> list = new List<FintechTankDeliveryForMagLinkModel>();
            StringBuilder builder = new StringBuilder();

            foreach (var row in dt.AsEnumerable().ToList())
            {
                var obj = new FintechTankDeliveryForMagLinkModel()
                {
                    ibank = row["ibank"] != null ? Convert.ToInt32(row["ibank"]) : 0,
                    Tank = row["Tank"] != null ? Convert.ToInt32(row["Tank"]) : 0,
                    StartVolume = row["StartVolume"] != null ? float.Parse(row["StartVolume"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    StartWater = row["StartWater"] != null ? float.Parse(row["StartWater"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    StartTemperature = row["StartTemperature"] != null ? float.Parse(row["StartTemperature"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    EndVolume = row["EndVolume"] != null ? float.Parse(row["EndVolume"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    EndWater = row["EndWater"] != null ? float.Parse(row["EndWater"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    EndTemperature = row["EndTemperature"] != null ? float.Parse(row["EndTemperature"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    Quantity = row["Quantity"] != null ? float.Parse(row["Quantity"].ToString(), CultureInfo.InvariantCulture) : 0.0F,
                    ConfirmedQuantity = !string.IsNullOrWhiteSpace(row["ConfirmedQuantity"].ToString()) ? float.Parse(row["ConfirmedQuantity"].ToString(), CultureInfo.InvariantCulture) : 0.0F
                };

                if (row["StartDateTime"] != null)
                    obj.StartDateTime = DateTime.Parse(row["StartDateTime"].ToString(), CultureInfo.InvariantCulture);
                if (row["EndDateTime"] != null)
                    obj.EndDateTime = DateTime.Parse(row["EndDateTime"].ToString(), CultureInfo.InvariantCulture);

                obj.OmniSiteId = map.getOmniSiteID(obj.ibank);
                obj.OmniAssetId_Tank = map.getOmniAssetID(obj.ibank, obj.Tank);
                if (obj.OmniAssetId_Tank > 0)
                {
                    obj.ProductNumber = map.getTank_ProductID(obj.OmniAssetId_Tank);
                    obj.ProductId = map.getArticleName(obj.OmniSiteId, obj.ProductNumber);
                }

                list.Add(obj);
            }

            list.RemoveAll(l => l.StartDateTime.Year == 1970 || l.EndDateTime.Year == 1970);
            WriteMagLinkRows(BuildMagLinkDeliveryTable(list, processedFileId), "Fintech_TankDelivery");
        }

        internal static DataTable BuildMagLinkStockTable(IEnumerable<FintechTankLevelsForMagLinkModel> items, int? fileId)
        {
            var table = MagLinkTable(
                ("Site_ID", typeof(int)), ("Tank_ID", typeof(int)), ("Product_ID", typeof(string)), ("Product_Number", typeof(int)),
                ("Product_Volume", typeof(float)), ("Product_Height", typeof(float)), ("Water_Volume", typeof(float)), ("Product_Temperature", typeof(float)),
                ("Time_Stamp", typeof(DateTime)), ("CreatedBy", typeof(string)), ("CreatedOn", typeof(DateTime)), ("FC_SiteID", typeof(int)),
                ("Omni_AssetID", typeof(int)), ("FC_TimeStamp", typeof(DateTime)), ("ProcessedFileID", typeof(int)));
            foreach (var item in items)
                table.Rows.Add(item.OmniSiteId, item.Tank, item.ProductId, item.ProductNumber, item.QTY, item.Height, item.Water,
                    item.Temperature, item.DateTime, "ETL", DateTime.UtcNow, item.ibank, item.OmniAssetId_Tank, item.DateTime, (object)fileId ?? DBNull.Value);
            return table;
        }

        internal static DataTable BuildMagLinkDeliveryTable(IEnumerable<FintechTankDeliveryForMagLinkModel> items, int? fileId)
        {
            var table = MagLinkTable(
                ("site_id", typeof(int)), ("tank_id", typeof(int)), ("date_start", typeof(DateTime)), ("time_start", typeof(TimeSpan)),
                ("vol_total", typeof(float)), ("date_end", typeof(DateTime)), ("time_end", typeof(TimeSpan)), ("vol_start", typeof(float)),
                ("vol_end", typeof(float)), ("temp_start", typeof(float)), ("temp_end", typeof(float)), ("water_start", typeof(float)),
                ("water_end", typeof(float)), ("FC_SiteID", typeof(int)), ("Omni_AssetID", typeof(int)), ("Product_ID", typeof(int)),
                ("Grade", typeof(string)), ("Confirmed_Quantity", typeof(float)), ("ProcessedFileID", typeof(int)));
            foreach (var item in items)
                table.Rows.Add(item.OmniSiteId, item.Tank, item.StartDateTime.Date, item.StartDateTime.TimeOfDay, item.Quantity,
                    item.EndDateTime.Date, item.EndDateTime.TimeOfDay, item.StartVolume, item.EndVolume, item.StartTemperature, item.EndTemperature,
                    item.StartWater, item.EndWater, item.ibank, item.OmniAssetId_Tank, item.ProductNumber, item.ProductId, item.ConfirmedQuantity, (object)fileId ?? DBNull.Value);
            return table;
        }

        private static DataTable MagLinkTable(params (string Name, Type Type)[] columns)
        {
            var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            foreach (var column in columns) table.Columns.Add(column.Name, column.Type);
            return table;
        }

        private void WriteMagLinkRows(DataTable rows, string table)
        {
            if (rows.Rows.Count == 0) return; // All epoch-1970 rows may have been filtered out.
            using var connection = new SqlConnection(connectToDb);
            connection.Open();
            using var transaction = System.Transactions.Transaction.Current == null ? connection.BeginTransaction() : null;
            using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.FireTriggers, transaction)
            { DestinationTableName = table, BatchSize = 1000, BulkCopyTimeout = 180 };
            foreach (DataColumn column in rows.Columns) copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            copy.WriteToServer(rows);
            transaction?.Commit();
            logger.Info($"MagLink: inserted {rows.Rows.Count} rows in {table}.");
        }

        public int ExecureSqlQuery(string sqlQuery)
        {
            int _rows = 0;
            
            try
            {
                // Execute on primary database
                using (SqlConnection connection = new SqlConnection(connectToDb))
                {
                    SqlCommand cmd = new SqlCommand()
                    {
                        CommandType = CommandType.Text,
                        Connection = connection,
                        CommandText = sqlQuery
                    };
                    connection.Open();
                    _rows = cmd.ExecuteNonQuery();
                    logger.Info($"Primary DB: {_rows} rows inserted via dynamic SQL");
                }
                
                // Execute on secondary database if enabled
                if (enableDualInsert && !string.IsNullOrEmpty(secondaryDbConnectionString))
                {
                    using (SqlConnection connection = new SqlConnection(secondaryDbConnectionString))
                    {
                        SqlCommand cmd = new SqlCommand()
                        {
                            CommandType = CommandType.Text,
                            Connection = connection,
                            CommandText = sqlQuery
                        };
                        connection.Open();
                        int secondaryRows = cmd.ExecuteNonQuery();
                        logger.Info($"Secondary DB: {secondaryRows} rows inserted via dynamic SQL");
                    }
                }
            }
            catch (Exception ex)
            {
                    if (ex is SqlException) throw;
                Console.WriteLine(ex.Message);
                logger.Error("ExecureSqlQuery failed: " + ex.Message, ex);
                FtpConnectivity.isExceptionOccured = true;
                throw;
            }
            
            return _rows;
        }

    }
}



