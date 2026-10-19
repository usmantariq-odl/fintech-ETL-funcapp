using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections;
using log4net;

namespace CSVReader
{
    class OmniFintechMapping
    {

        private DataTable customerIDs = new DataTable();
        private DataTable productIDs = new DataTable();
        private DataTable articles = new DataTable();
        private DataTable omniSiteID = new DataTable();
        private DataTable productNames = new DataTable();

        private DataTable Pumps = new DataTable();

        public static string connectToDb = "";

        SqlConnection conn;
        SqlCommand cmd;
        SqlDataAdapter da;
        ILog logger;

        public OmniFintechMapping(ILog log)
        {
            logger = log;
            logger.Info("creating OmniFintechMapping object.");
            
            // Read database connection string from config (same as primary ETL database)
            connectToDb = EtlSettings.Load().SqlConnectionString;
            if (string.IsNullOrEmpty(connectToDb))
            {
                logger.Error("CRITICAL: PrimaryDbConnectionString not found in app.config! ETL cannot proceed.");
                throw new InvalidOperationException("FintechSqlConnectionString is missing");
            }
            logger.Info("OmniConnect mapping using Primary database connection string from config.");
            
            conn = new SqlConnection(connectToDb);
            //get CustomerIDs
            logger.Info("getting Omni_Fintech_SiteMapping");

            //BackUp
            //cmd = new SqlCommand("SELECT * FROM Omni_Fintech_SiteMapping", conn);

            //New Query
            cmd = new SqlCommand(@"SELECT CustomerId,IsActive,fsm.ID,SiteId,FC_SiteID FROM FintechMappedCustomers fmc
								JOIN Omni_Fintech_SiteMapping fsm
								ON fmc.SiteId=fsm.Omni_SiteID
								WHERE IsActive=1 and SiteId is not null", conn); //check if customer is active and site is active as well

            conn.Open();
            try
            {
            da = new SqlDataAdapter(cmd);
            da.Fill(omniSiteID);


            logger.Info("getting Omni_Fintech_TankMapping");
            //get Tank Product IDs
            cmd = new SqlCommand("SELECT * FROM Omni_Fintech_TankMapping", conn);
            da = new SqlDataAdapter(cmd);
            da.Fill(productIDs);

            /*
            logger.writelog("getting Omni_Fintech_Products");
            //get Article Name against Product ID
            cmd = new SqlCommand("SELECT * FROM Omni_Fintech_Products", conn);
            da = new SqlDataAdapter(cmd);
            da.Fill(articles);
            */

            logger.Info("getting Plant_Site");
            //get omni siteid against fc siteid

            //BackUp
            //cmd = new SqlCommand("SELECT * FROM Plant_Site where customer_id_fk = 59  or customer_id_fk = 67 or customer_id_fk = 98 or customer_id_fk = 205 or customer_id_fk = 206 ", conn);

            //New Query
            cmd = new SqlCommand(@"SELECT * FROM Plant_Site 
                                 WHERE customer_id_fk IN 
                                 (SELECT CustomerId FROM FintechMappedCustomers
                                 WHERE IsActive = 1)", conn);
            da = new SqlDataAdapter(cmd);
            da.Fill(customerIDs);


            logger.Info("getting Omni_Fintech_Products");
            //get Product Names for each customer
            cmd = new SqlCommand("SELECT * FROM Omni_Fintech_Products", conn);
            da = new SqlDataAdapter(cmd);
            da.Fill(productNames);


            logger.Info("getting Omni_Fintech_PumpMapping");
            //get Pump IDs for each customer
            cmd = new SqlCommand("select * from Omni_Fintech_PumpMapping", conn);
            da = new SqlDataAdapter(cmd);
            da.Fill(Pumps);
            }
            finally { conn.Dispose(); }
        }


        public int getOmniCustomerID(int Omni_SiteID)
        {
            string _sqlWhere = "Site_Id = " + Convert.ToString(Omni_SiteID);
            var table = customerIDs.Select(_sqlWhere).AsEnumerable().First();

            return table.Field<int>(10);
        }
        public int getOmniAssetID(int FintechsiteID, int TankID)
        {
            string _sqlWhere = "FC_SiteID = " + Convert.ToString(FintechsiteID) + " and FC_TankID = " + Convert.ToString(TankID);
            var table = productIDs.Select(_sqlWhere).AsEnumerable().FirstOrDefault();

            if (table == null)
                return 0;

            return table.Field<int>(3);
        }

        /*returns a list of all the tanks of the given Omni_SiteID */
        public List<int> getTanks(int Omni_SiteID)
        {
            List<int> tankids = new List<int>();
            string _sqlWhere = "Omni_SiteID = " + Convert.ToString(Omni_SiteID);
            var table = productIDs.Select(_sqlWhere);

            List<int> tanks = new List<int>();

            foreach (var i in table)
            {
                tanks.Add(Convert.ToInt32(i.Field<int>(0)));
            }

            return tanks;

        }

        public int getOmniSiteID(int FintechSiteID)
        {
            try
            {
                string _sqlWhere = "FC_SiteID = " + Convert.ToString(FintechSiteID);
                var table = omniSiteID.Select(_sqlWhere).AsEnumerable().First();
                var isActive = table.Field<bool>(1);
                if (isActive)
                    return table.Field<int>(3);
                else
                    return 0;
            }
            catch
            {
                return -1;
            }

        }

        public string getArticleName(int omniSiteID, int productid)
        {
            int customerID = getOmniCustomerID(omniSiteID);

            string _sqlWhere = "product_id = " + Convert.ToString(productid) + " and OmniCustomerID_FK = " + Convert.ToString(customerID);
            var table = productNames.Select(_sqlWhere).AsEnumerable().FirstOrDefault();

            if (table == null)
                return null;

            return table.Field<string>(2);
        }

        public int getTank_ProductID(int OmniAssetID)
        {
            string _sqlWhere = "Omni_AssetID = " + Convert.ToString(OmniAssetID);
            var table = productIDs.Select(_sqlWhere).AsEnumerable().First();

            return table.Field<int>(9);
        }

        //OLD CODE LOGIC
        //public void checkValidDeliveryMapping(DataTable dt)
        //{
        //    // Legacy TriggerLogicApp path is intentionally disabled.
        //    //TriggerLogicApp trlapp = new TriggerLogicApp();
        //    DataView view = new DataView(dt);
        //    DataTable distinctValues = view.ToTable(true, "site_id", "tank_id");

        //    if (distinctValues.Rows.Count == 0)
        //        return;


        //    string _sqlWhere = "FC_SiteID = " + Convert.ToString(distinctValues.Rows[0][0]);
        //    DataRow[] ctanks = productIDs.Select(_sqlWhere);

        //    DataTable configtanks = ctanks.CopyToDataTable();

        //    //if no tanks are configured for the site
        //    if (ctanks.Length == 0)
        //    {
        //        Console.WriteLine("Configuration not found. Triggering Alarm");
        //        //trlapp.triggermisconfiguration(Convert.ToString(distinctValues.Rows[0][0]), "No tanks configured for the site. ");
        //        return;

        //    }




        //    for (int i = 0; i < distinctValues.Rows.Count; i++)
        //    {
        //        var datarow = distinctValues.Rows[i];
        //        int tkid = Convert.ToInt32(datarow["tank_id"]);
        //        _sqlWhere = "FC_TankID = " + Convert.ToString(datarow["tank_id"]);
        //        DataRow[] table = configtanks.Select(_sqlWhere);

        //        //if the given tank is not configured
        //        if (table.Length == 0)
        //        {
        //            Console.WriteLine("Mapping mismatch in Tank Status File. Triggering Alarm");
        //            //trlapp.triggermisconfiguration(Convert.ToString(datarow["site_id"]), "Tank " + Convert.ToString(datarow["tank_id"]) + " not configured.");
        //        }



        //    }
        //}
        //public void checkValidTransactionsType(DataTable dt)
        //{

        //    // Legacy TriggerLogicApp path is intentionally disabled.
        //    //TriggerLogicApp trlapp = new TriggerLogicApp();
        //    DataView view = new DataView(dt);
        //    DataTable distinctValues = view.ToTable(true, "site_id");

        //    if (distinctValues.Rows.Count == 0)
        //        return;

        //    string _sqlWhere = "FC_SiteID = " + Convert.ToString(distinctValues.Rows[0][0]);

        //    DataRow[] table = omniSiteID.Select(_sqlWhere);

        //    if (table.Length == 0)
        //    {
        //        Console.WriteLine("Configuration not found. Triggering Alarm");
        //        //trlapp.triggermisconfiguration(Convert.ToString(distinctValues.Rows[0][0]), "FC Site " + Convert.ToString(distinctValues.Rows[0][0]) + " doesn't exist. ");
        //        return;
        //    }


        //}
        //public void checkValidTankMapping(DataTable dt)
        //{
        //    // Legacy TriggerLogicApp path is intentionally disabled.
        //    //TriggerLogicApp trlapp = new TriggerLogicApp();
        //    DataView view = new DataView(dt);
        //    DataTable distinctValues = view.ToTable(true, "site_id", "tank_id", "product_id", "product_number");



        //    string _sqlWhere = "FC_SiteID = " + Convert.ToString(distinctValues.Rows[0][0]);
        //    DataRow[] ctanks = productIDs.Select(_sqlWhere);
        //    DataTable configtanks = ctanks.CopyToDataTable();

        //    //if now tanks are configured
        //    if (ctanks.Length == 0)
        //    {
        //        Console.WriteLine("Configuration not found. Triggering Alarm");
        //        //trlapp.triggermisconfiguration(Convert.ToString(distinctValues.Rows[0][0]), "No tanks configured for the site " + Convert.ToString(distinctValues.Rows[0][0]) + " .");
        //        return;

        //    }

        //    for (int i = 0; i < distinctValues.Rows.Count; i++)
        //    {
        //        var datarow = distinctValues.Rows[i];
        //        _sqlWhere = "FC_TankID = " + Convert.ToString(datarow["tank_id"]);
        //        DataRow[] table = configtanks.Select(_sqlWhere);

        //        //if the given tank is not configured
        //        if (table.Length == 0)
        //        {
        //            Console.WriteLine("Mapping mismatch in Tank Status File. Triggering Alarm");
        //            //trlapp.triggermisconfiguration(Convert.ToString(datarow["site_id"]), "Tank " + Convert.ToString(datarow["tank_id"]) + " not configured.");
        //        }
        //        else
        //        {
        //            //if the received tank was configured for a different product number


        //            //get received product number                             //get configured product number
        //            if (Convert.ToInt32(datarow["product_number"]) != Convert.ToInt32(table[0]["ProdID_FK"]))
        //            {
        //                Console.WriteLine("Mapping mismatch in Tank Status File. Triggering Alarm");
        //                //trlapp.triggermisconfiguration(Convert.ToString(datarow["site_id"]), "Tank " + Convert.ToString(datarow["tank_id"]) + " is configured for Product ID " + Convert.ToString(table[0]["ProductID_FK"]) + " but " + Convert.ToString(datarow["product_number"]) + " was received.");
        //            }

        //        }

        //    }
        //}
        //public void checkValidTransactionMapping(DataTable dt)
        //{
        //    DataView view = new DataView(dt);
        //    // Legacy TriggerLogicApp path is intentionally disabled.
        //    //TriggerLogicApp trlapp = new TriggerLogicApp();

        //    //get distinct values of the keys in the files
        //    DataTable distinctValues = view.ToTable(true, "site_id", "pump_id", "hose_id", "grade_id");

        //    for (int i = 0; i < distinctValues.Rows.Count; i++)
        //    {
        //        var datarow = distinctValues.Rows[i];
        //        string _sqlWhere = "FC_SiteID = " + Convert.ToString(datarow["site_id"]) + " and FC_PumpID = " + Convert.ToString(datarow["pump_id"]) + " and FC_NozzleID = " + Convert.ToString(datarow["hose_id"]);
        //        DataRow[] table = Pumps.Select(_sqlWhere);

        //        if (table.Length == 0)
        //        {
        //            Console.WriteLine("Mapping mismatch in Transactions File. Triggering Alarm");
        //            //trlapp.triggermisconfiguration(Convert.ToString(datarow["site_id"]), "No Pumps/Nozzles Configuration Found!");

        //        }
        //        else
        //        {
        //            int grade_id = Convert.ToInt32(datarow["grade_id"]);
        //            int tankid = Convert.ToInt32(table[0]["Omni_TankID"]);

        //            _sqlWhere = "Omni_AssetID = " + Convert.ToString(tankid);
        //            table = productIDs.Select(_sqlWhere);

        //            if (table.Length == 0)
        //            {
        //                Console.WriteLine("Mapping mismatch in Transactions File. Triggering Alarm");
        //                //trlapp.triggermisconfiguration(Convert.ToString(datarow["site_id"]), Convert.ToString(tankid) + " no product found against the configured tank asset id.");
        //            }
        //            else
        //            {
        //                DataRow dr = table[0];
        //                Console.WriteLine(table[0]["ProdID_FK"]);
        //                if (grade_id != Convert.ToInt32(table[0]["ProdID_FK"]))
        //                {
        //                    Console.WriteLine("Mapping mismatch in transactions file. Trigger Alarm");
        //                    //trlapp.triggermisconfiguration(Convert.ToString(datarow["site_id"]), "Mapping mismatch in Transactions File received from the file  " + Convert.ToString(datarow["site_id"]) + " .");

        //                }

        //            }

        //        }

        //    }

        //}

    }
}
