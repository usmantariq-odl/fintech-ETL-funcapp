# Alson grade follow-up for the dashboard (read-only findings)

No dashboard, API or reporting procedure changes were made for this task.

Raw Alson transactions now store H=1 Excellium, R=2 Hi Super, D=3 Diesel in Grade_ID.
Article holds the correct shared product name. The original customer227 product definitions
remain product_id1 Hi Super,2 Diesel,3 Excellium. ProductMap.ProductRowID links to those
existing definition rows; AlsonGradeID is a separate vendor-specific key.

## Required lookup rule

For an Alson site (current DeviceTypeId3), resolve transaction Grade_ID against
Alson_Fintech_ProductMap.AlsonGradeID for the site's customer, then join ProductRowID to
Omni_Fintech_Products.Id. For Dover keep the existing customer + product_id lookup.
Never fall back to the shared numeric ID for an unmapped Alson grade, because that produces
a plausible but incorrect label. Article can be used as the raw sale's known name.

The customer-only dropdown currently returns the shared numeric IDs. It must become aware
of selected site/device type, or retain a canonical product selection and translate filters
per source in the API. Mixed Dover/Alson site selections need per-site translation; grade1
cannot mean the same product for both sources. Do not change original customer product rows.

## Exact source paths inspected

* FintechDashboard/src/app/shared/services/transaction.service.ts:33:
  getGradeByCustomerId calls Product/{customerId} without site/device context.
* FintechDashboard/src/app/shared/components/inline-filter/inline-filter.component.ts:791:
  getProducts uses that customer-only endpoint for shared filters.
* FintechFuelAPI/FintechConfigurationAPI.Repository/Concrete/ProductRepository.cs:23:
  GetProductsByCustomerIDAsync reads the unchanged shared product definitions.
* FintechDashboard/src/app/pages/loss-gain-charts/loss-gain-charts.component.ts:502:
  loads the same grade list and uses product IDs for reconciliation requests/labels.
* FintechDashboard/src/app/pages/transactions/transactions.component.ts:
  passes requestModel.gradeId as the product filter to transaction/reconciliation APIs.
* FintechDashboard/src/app/shared/services/tank.service.ts:131:
  resolves grade IDs through the supplied shared grade list; review this before using
  Alson-native numbers for tanks. ATG products are still unassigned and were not changed.

## SQL/reporting paths to adapt later

* GetHourlyTransactionDetails directly joins raw Grade_ID to shared product_id.
* GetTransactionDetails does the same for TRANSACTION_CID_67_hourly aggregates.
* Fintech_runtime_calc and Fintech_runtime_booked_recon resolve product names through
  Omni_Fintech_Products and compare grades with tank/reconciliation product numbers.
* FintechTransactionRepository calls these procedures and passes the product filter through.

These paths need Alson-aware names and filter translation. Sales/tank reconciliation must
also translate Alson grade numbers when comparing to the existing canonical ProdID_FK.
This is identified follow-up, not implemented functionality. Alson remains disabled until
the user chooses to activate it and complete the remaining portal/configuration work.

## Completed within the ETL task

Only Alson ProductMap schema/configuration and the 100 linked Alson transaction Grade_IDs
were changed in staging. Shared product rows were snapshot-checked unchanged. No target
schema change, no Dover row update, no tank-product change or report rebuild. Migration,
pre-change backups, test evidence and the updated rollback manifest are in deployment/
and artifacts/alson/grades-update/ respectively.
