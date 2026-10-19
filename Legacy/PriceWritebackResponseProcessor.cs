using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;
using log4net;
using Newtonsoft.Json.Linq;

namespace CSVReader
{
    /// <summary>Processes Dover price writeback response files: RESFPR, PCA, PCD, PCH, GRP.</summary>
    class PriceWritebackResponseProcessor
    {
        private readonly ILog _log;
        private readonly string _connStr;
        private readonly int? _fcSiteId;

        private const string FunctionAppUserId = "ETL";

        public PriceWritebackResponseProcessor(ILog log, int? fcSiteId = null)
        {
            _log = log;
            _connStr = DAL.connectToDb;
            _fcSiteId = fcSiteId;
        }

        // ── Public entry points ────────────────────────────────────────────────

        public void ProcessResFpr(string filePath)
        {
            _log.Info($"Processing RESFPR file: {filePath}");
            var rows = ReadCsv(filePath);

            foreach (DataRow row in rows.Rows)
            {
                // file_exchange_id = {fcSiteId}_{siteId}_{requestId}
                var fileExchangeId = row["file_exchange_id"].ToString();
                var parts = fileExchangeId.Split('_');
                if (_fcSiteId.HasValue && (!int.TryParse(parts[0], out var responseSite) || responseSite != _fcSiteId.Value))
                    throw new InvalidDataException("RESFPR file_exchange_id site does not match its input folder.");
                if (parts.Length < 3 || !long.TryParse(parts[parts.Length - 1], out long requestId))
                {
                    throw new InvalidDataException($"RESFPR file_exchange_id is invalid: {fileExchangeId}");
                }

                var status = row["status"].ToString().Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(status))
                {
                    throw new InvalidDataException($"RESFPR status is missing for request {requestId}.");
                }

                if (status == "OK")
                {
                    if (!long.TryParse(row["fusion_id"].ToString(), out long fusionId) || fusionId <= 0)
                    {
                        throw new InvalidDataException($"RESFPR fusion_id is invalid for successful request {requestId}.");
                    }

                    var message = $"RESFPR received. fusion_id={fusionId}";
                    AdvanceStatus(requestId, "QueuedAtDover", message, fusionId);
                    _log.Info($"RequestId={requestId} advanced to QueuedAtDover. DoverPriceChangeId={fusionId}");
                }
                else
                {
                    FailRequest(requestId, $"RESFPR status={status}.");
                    _log.Warn($"RequestId={requestId} failed. RESFPR status={status}");
                }
            }
        }

        public void ProcessPca(string filePath)
        {
            _log.Info($"Processing PCA file: {filePath}");
            var rows = ReadCsv(filePath);

            foreach (DataRow row in rows.Rows)
            {
                var priceChangeId = Convert.ToInt64(row["price_change_id"]);
                var approvalStatus = row["approval_status"].ToString();
                var message = $"PCA received. approval_status={approvalStatus}";

                InsertHistoryByDoverPriceChangeId(priceChangeId, "QueuedAtDover", message, filePath);
            }
        }

        public void ProcessPcd(string filePath)
        {
            _log.Info($"Processing PCD file: {filePath}");
            var rows = ReadCsv(filePath);

            long priceChangeId = 0;
            var failureReasons = new List<string>();
            var validationResults = new List<string>();

            foreach (DataRow row in rows.Rows)
            {
                priceChangeId = Convert.ToInt64(row["price_change_id"]);
                var gradeId = row["grade_id"].ToString();
                var validation = row["price_validation"].ToString().Trim().ToUpperInvariant();
                var validationResult = $"grade_id={gradeId}: price_validation={validation}";

                validationResults.Add(validationResult);

                if (validation != "OK" && validation != "SSF_TRANS_PRICES_NO_CHANGES")
                {
                    failureReasons.Add(validationResult);
                }
            }

            if (priceChangeId == 0)
            {
                throw new InvalidDataException("PCD contains no valid price_change_id.");
            }

            if (failureReasons.Count > 0)
                FailRequestByDoverPriceChangeId(priceChangeId, $"PCD validation failed. {string.Join("; ", failureReasons)}", filePath);
            else
                InsertHistoryByDoverPriceChangeId(priceChangeId, "QueuedAtDover", $"PCD validation complete. {string.Join("; ", validationResults)}", filePath);
        }

        public void ProcessPch(string filePath)
        {
            _log.Info($"Processing PCH file: {filePath}");
            var rows = ReadCsv(filePath);

            foreach (DataRow row in rows.Rows)
            {
                var priceChangeId = Convert.ToInt64(row["price_change_id"]);
                var result = row["price_change_result"].ToString().Trim().ToUpperInvariant();
                var message = $"PCH price_change_result={result}";

                if (result == "OK"
                    || result == "SSF_TRANS_PRICES_NO_CHANGES"
                    || result == "SSF_PARTIAL_IGNORED-SSF_TRANS_PRICES_NO_CHANGES")
                {
                    CompleteRequestByDoverPriceChangeId(priceChangeId, message, filePath);
                    _log.Info($"DoverPriceChangeId={priceChangeId} completed successfully.");
                }
                else
                {
                    FailRequestByDoverPriceChangeId(priceChangeId, message, filePath);
                    _log.Warn($"DoverPriceChangeId={priceChangeId} failed. Result={result}");
                }
            }
        }

        public void ProcessGrp(string filePath)
        {
            _log.Info($"Processing GRP file: {filePath}");
            var rows = ReadCsv(filePath);

            foreach (DataRow row in rows.Rows)
            {
                var priceChangeId = Convert.ToInt64(row["price_change_id"]);
                var gradeId = row["grade_id"].ToString();
                var ppu = row["ppu"].ToString();
                var message = $"GRP snapshot: grade_id={gradeId} ppu={ppu}";

                InsertGrpHistoryByDoverPriceChangeId(priceChangeId, gradeId, ppu, message, filePath);
            }
        }

        // ── DB operations ──────────────────────────────────────────────────────

        private void AdvanceStatus(long requestId, string newStatus, string message, long doverPriceChangeId)
        {
            using (var conn = new SqlConnection(_connStr))
            {
                conn.Open();

                using (var cmd = new SqlCommand(
                    @"UPDATE PriceWritebackRequests
                      SET Status = @Status, DoverPriceChangeId = @DoverPriceChangeId
                      WHERE RequestId = @RequestId", conn))
                {
                    cmd.Parameters.AddWithValue("@Status", newStatus);
                    cmd.Parameters.AddWithValue("@DoverPriceChangeId", doverPriceChangeId);
                    cmd.Parameters.AddWithValue("@RequestId", requestId);
                    cmd.ExecuteNonQuery();
                }

                InsertHistory(conn, requestId, newStatus, message);
            }
        }

        private void FailRequest(long requestId, string reason)
        {
            using (var conn = new SqlConnection(_connStr))
            {
                conn.Open();
                UpdateStatus(conn, requestId, "Failed", setCompletedOn: true);
                InsertHistory(conn, requestId, "Failed", reason);
            }
        }

        private void CompleteRequestByDoverPriceChangeId(long doverPriceChangeId, string message, string filePath)
        {
            using (var conn = new SqlConnection(_connStr))
            {
                conn.Open();
            long? requestId = EnsureRequestByDoverPriceChangeId(conn, doverPriceChangeId, filePath);
                if (requestId == null) { _log.Warn($"No request found for DoverPriceChangeId={doverPriceChangeId}"); return; }

                UpdateStatus(conn, requestId.Value, "Completed", setCompletedOn: true);
                InsertHistory(conn, requestId.Value, "Completed", message);
            }
        }

        private void FailRequestByDoverPriceChangeId(long doverPriceChangeId, string reason, string filePath)
        {
            using (var conn = new SqlConnection(_connStr))
            {
                conn.Open();
            long? requestId = EnsureRequestByDoverPriceChangeId(conn, doverPriceChangeId, filePath);
                if (requestId == null) { _log.Warn($"No request found for DoverPriceChangeId={doverPriceChangeId}"); return; }

                UpdateStatus(conn, requestId.Value, "Failed", setCompletedOn: true);
                InsertHistory(conn, requestId.Value, "Failed", reason);
            }
        }

        private void InsertHistoryByDoverPriceChangeId(long doverPriceChangeId, string status, string message, string filePath)
        {
            using (var conn = new SqlConnection(_connStr))
            {
                conn.Open();
            long? requestId = EnsureRequestByDoverPriceChangeId(conn, doverPriceChangeId, filePath);
                if (requestId == null) { _log.Warn($"No request found for DoverPriceChangeId={doverPriceChangeId}"); return; }

                // Use current status if no override provided
                var effectiveStatus = status ?? GetCurrentStatus(conn, requestId.Value);
                InsertHistory(conn, requestId.Value, effectiveStatus, message);
            }
        }

        private void InsertGrpHistoryByDoverPriceChangeId(long doverPriceChangeId, string gradeId, string ppu, string message, string filePath)
        {
            using (var conn = new SqlConnection(_connStr))
            {
                conn.Open();
                long? requestId = EnsureRequestByDoverPriceChangeId(conn, doverPriceChangeId, filePath);
                if (requestId == null) { _log.Warn($"No request found for DoverPriceChangeId={doverPriceChangeId}"); return; }

                UpdateManualPricePayload(conn, requestId.Value, gradeId, ppu);
                InsertHistory(conn, requestId.Value, GetCurrentStatus(conn, requestId.Value), message);
            }
        }

        private long? EnsureRequestByDoverPriceChangeId(SqlConnection conn, long doverPriceChangeId, string filePath)
        {
            int? fcSiteId = ParseFcSiteId(filePath);
            int? siteId = fcSiteId == null ? null : GetSiteIdByFcSiteId(conn, fcSiteId.Value);
            long? requestId = GetRequestIdByDoverPriceChangeId(conn, doverPriceChangeId, siteId);
            if (requestId == null)
            {
                if (fcSiteId == null)
                {
                    _log.Warn($"Cannot create Manual price writeback request. FC site ID was not found in {Path.GetFileName(filePath)}");
                }
                else
                {
                    requestId = CreateManualRequest(conn, doverPriceChangeId, fcSiteId.Value);
                }
            }

            return requestId;
        }

        private long? CreateManualRequest(SqlConnection conn, long doverPriceChangeId, int fcSiteId)
        {
            int? siteId = null;
            int? customerId = null;

            using (var mappingCmd = new SqlCommand(
                                @"SELECT TOP 1 site.Site_Id AS SiteId, site.Customer_Id_FK
                  FROM Omni_Fintech_SiteMapping mapping
                  INNER JOIN Plant_Site site ON site.Site_Id = mapping.Omni_SiteID
                                    WHERE mapping.FC_SiteID = @FcSiteId
                                        AND site.Customer_Id_FK IS NOT NULL", conn))
            {
                mappingCmd.Parameters.Add("@FcSiteId", SqlDbType.Int).Value = fcSiteId;
                using (var reader = mappingCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        siteId = Convert.ToInt32(reader["SiteId"]);
                        customerId = Convert.ToInt32(reader["Customer_Id_FK"]);
                    }
                }
            }

            long? requestId = null;
            if (siteId == null || customerId == null)
            {
                _log.Warn($"Cannot create Manual price writeback request. No active site mapping found for FC site ID={fcSiteId}");
            }
            else
            {
                using (var insertCmd = new SqlCommand(
                    @"INSERT INTO PriceWritebackRequests
                          (CustomerId, SiteId, RequestedByUserId, Remarks, Status, Type, PricePayloadJson,
                           EffectiveTime, CreatedOn, CreatedBy, DoverPriceChangeId)
                      OUTPUT INSERTED.RequestId
                      VALUES
                          (@CustomerId, @SiteId, @RequestedByUserId, @Remarks, @Status, @Type, @PricePayloadJson,
                           DATEADD(HOUR, 5, GETUTCDATE()), GETUTCDATE(), @CreatedBy, @DoverPriceChangeId)", conn))
                {
                    insertCmd.Parameters.Add("@CustomerId", SqlDbType.Int).Value = customerId.Value;
                    insertCmd.Parameters.Add("@SiteId", SqlDbType.Int).Value = siteId.Value;
                    insertCmd.Parameters.Add("@RequestedByUserId", SqlDbType.NVarChar, 450).Value = "Station";
                    insertCmd.Parameters.Add("@Remarks", SqlDbType.NVarChar, 1000).Value = "Station-initiated Dover price change.";
                    insertCmd.Parameters.Add("@Status", SqlDbType.NVarChar, 50).Value = "QueuedAtDover";
                    insertCmd.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = "Manual";
                    insertCmd.Parameters.Add("@PricePayloadJson", SqlDbType.NVarChar, -1).Value = "[]";
                    insertCmd.Parameters.Add("@CreatedBy", SqlDbType.NVarChar, 450).Value = FunctionAppUserId;
                    insertCmd.Parameters.Add("@DoverPriceChangeId", SqlDbType.BigInt).Value = doverPriceChangeId;
                    requestId = Convert.ToInt64(insertCmd.ExecuteScalar());
                }

                _log.Info($"Created Manual price writeback RequestId={requestId} for FC site ID={fcSiteId}, DoverPriceChangeId={doverPriceChangeId}");
            }

            return requestId;
        }

        private int? ParseFcSiteId(string filePath)
        {
            if (_fcSiteId.HasValue) return _fcSiteId;
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string[] prefixes = { "PCA-", "PCD-", "PCH-", "GRP-" };
            int parsedSiteId = 0;
            bool parsed = false;

            foreach (string prefix in prefixes)
            {
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && fileName.Length >= prefix.Length + 6)
                {
                    parsed = int.TryParse(fileName.Substring(prefix.Length, 6), out parsedSiteId);
                    break;
                }
            }

            return parsed ? (int?)parsedSiteId : null;
        }

        private static void UpdateManualPricePayload(SqlConnection conn, long requestId, string gradeId, string ppu)
        {
            if (!int.TryParse(gradeId, out int productId)
                || !decimal.TryParse(ppu, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal newPrice))
            {
                return;
            }

            string payloadJson;
            using (var selectCmd = new SqlCommand(
                "SELECT PricePayloadJson FROM PriceWritebackRequests WHERE RequestId = @RequestId AND Type = 'Manual'", conn))
            {
                selectCmd.Parameters.Add("@RequestId", SqlDbType.BigInt).Value = requestId;
                payloadJson = selectCmd.ExecuteScalar()?.ToString();
            }

            if (payloadJson == null)
            {
                return;
            }

            JArray payload;
            try
            {
                payload = JArray.Parse(payloadJson);
            }
            catch
            {
                payload = new JArray();
            }

            JObject matchingItem = null;
            foreach (JToken item in payload)
            {
                if (item["productId"]?.Value<int>() == productId)
                {
                    matchingItem = item as JObject;
                    break;
                }
            }

            if (matchingItem == null)
            {
                payload.Add(new JObject
                {
                    ["productId"] = productId,
                    ["newPrice"] = newPrice
                });
            }
            else
            {
                matchingItem["newPrice"] = newPrice;
            }

            using (var updateCmd = new SqlCommand(
                "UPDATE PriceWritebackRequests SET PricePayloadJson = @PricePayloadJson WHERE RequestId = @RequestId AND Type = 'Manual'", conn))
            {
                updateCmd.Parameters.Add("@RequestId", SqlDbType.BigInt).Value = requestId;
                updateCmd.Parameters.Add("@PricePayloadJson", SqlDbType.NVarChar, -1).Value = payload.ToString(Newtonsoft.Json.Formatting.None);
                updateCmd.ExecuteNonQuery();
            }
        }

        private static int? GetSiteIdByFcSiteId(SqlConnection conn, int fcSiteId)
        {
            using (var cmd = new SqlCommand(
                @"SELECT TOP 1 site.Site_Id
                  FROM Omni_Fintech_SiteMapping mapping
                  INNER JOIN Plant_Site site ON site.Site_Id = mapping.Omni_SiteID
                  WHERE mapping.FC_SiteID = @FcSiteId
                    AND site.Customer_Id_FK IS NOT NULL", conn))
            {
                cmd.Parameters.Add("@FcSiteId", SqlDbType.Int).Value = fcSiteId;
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? (int?)null : Convert.ToInt32(result);
            }
        }

        private static long? GetRequestIdByDoverPriceChangeId(SqlConnection conn, long doverPriceChangeId, int? siteId)
        {
            if (siteId == null)
            {
                return null;
            }

            using (var cmd = new SqlCommand(
                @"SELECT TOP 1 RequestId
                  FROM PriceWritebackRequests
                  WHERE DoverPriceChangeId = @Id
                    AND SiteId = @SiteId
                  ORDER BY RequestId DESC", conn))
            {
                cmd.Parameters.Add("@Id", SqlDbType.BigInt).Value = doverPriceChangeId;
                cmd.Parameters.Add("@SiteId", SqlDbType.Int).Value = siteId.Value;
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? (long?)null : Convert.ToInt64(result);
            }
        }

        private static string GetCurrentStatus(SqlConnection conn, long requestId)
        {
            using (var cmd = new SqlCommand(
                "SELECT Status FROM PriceWritebackRequests WHERE RequestId = @Id", conn))
            {
                cmd.Parameters.AddWithValue("@Id", requestId);
                return cmd.ExecuteScalar()?.ToString() ?? "Unknown";
            }
        }

        private static void UpdateStatus(SqlConnection conn, long requestId, string status, bool setCompletedOn)
        {
            var sql = setCompletedOn
                ? "UPDATE PriceWritebackRequests SET Status = @Status, CompletedOn = GETUTCDATE() WHERE RequestId = @RequestId"
                : "UPDATE PriceWritebackRequests SET Status = @Status WHERE RequestId = @RequestId";

            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@Status", status);
                cmd.Parameters.AddWithValue("@RequestId", requestId);
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertHistory(SqlConnection conn, long requestId, string status, string message)
        {
            using (var cmd = new SqlCommand(
                                @"INSERT INTO PriceWritebackStatusHistory (RequestId, Status, Message, ChangedByUserId, ChangedAtUtc)
                                    SELECT @RequestId, @Status, @Message, @ChangedBy, GETUTCDATE()
                                    WHERE NOT EXISTS (
                                            SELECT 1
                                            FROM PriceWritebackStatusHistory WITH (UPDLOCK, HOLDLOCK)
                                            WHERE RequestId = @RequestId
                                                AND Status = @Status
                                                AND ChangedByUserId = @ChangedBy
                                                AND (Message = @Message OR (Message IS NULL AND @Message IS NULL))
                                    )", conn))
            {
                cmd.Parameters.AddWithValue("@RequestId", requestId);
                cmd.Parameters.AddWithValue("@Status", status);
                cmd.Parameters.AddWithValue("@Message", (object)message ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ChangedBy", FunctionAppUserId);
                cmd.ExecuteNonQuery();
            }
        }

        // ── CSV helper ─────────────────────────────────────────────────────────

        private DataTable ReadCsv(string filePath)
        {
            var dataTable = new DataTable();
            var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                TrimOptions = TrimOptions.Trim
            };

            using (var reader = new StreamReader(filePath))
            using (var csv = new CsvReader(reader, configuration))
            {
                if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord == null)
                {
                    throw new InvalidDataException($"CSV file {Path.GetFileName(filePath)} has no header row.");
                }

                foreach (var header in csv.HeaderRecord)
                {
                    if (string.IsNullOrWhiteSpace(header))
                    {
                        throw new InvalidDataException($"CSV file {Path.GetFileName(filePath)} contains an empty header.");
                    }

                    dataTable.Columns.Add(header.Trim(), typeof(string));
                }

                while (csv.Read())
                {
                    var row = dataTable.NewRow();
                    for (int index = 0; index < dataTable.Columns.Count; index++)
                    {
                        row[index] = csv.GetField(index);
                    }

                    dataTable.Rows.Add(row);
                }
            }

            if (dataTable.Rows.Count == 0)
            {
                throw new InvalidDataException($"CSV file {Path.GetFileName(filePath)} contains no data rows.");
            }

            return dataTable;
        }
    }
}
