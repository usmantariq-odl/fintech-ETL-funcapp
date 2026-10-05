using System;

namespace CSVReader.models
{
    public class FintechTankLevelsForMagLinkModel
    {
        public int ibank { get; set; }
        public DateTime DateTime { get; set; }
        public int Tank { get; set; }
        public float QTY { get; set; }
        public float Temperature { get; set; }
        public float Water { get; set; }
        public float Height { get; set; }

        public int OmniSiteId { get; set; }
        public int OmniAssetId_Tank { get; set; }
        public string ProductId { get; set; }
        public int ProductNumber { get; set; }
    }
}
