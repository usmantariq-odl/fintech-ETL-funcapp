using System;

namespace CSVReader.models
{
    public class FintechTankDeliveryForMagLinkModel
    {
        public int ibank { get; set; }
        public int Tank { get; set; }
        public DateTime StartDateTime { get; set; }
        public float StartVolume { get; set; }
        public float StartWater { get; set; }
        public float StartTemperature { get; set; }
        public DateTime EndDateTime { get; set; }
        public float EndVolume { get; set; }
        public float EndWater { get; set; }
        public float EndTemperature { get; set; }
        public float Quantity { get; set; }
        public float ConfirmedQuantity { get; set; }

        public int OmniSiteId { get; set; }
        public int OmniAssetId_Tank { get; set; }
        public string ProductId { get; set; }
        public int ProductNumber { get; set; }
    }
}
