public class SiteAndProductResponse
{
    public int SiteId { get; set; }
    public int CustomerId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
}

public class SiteMetaDataResponse
{
    public int? FCSiteId { get; set; }
    public int? FintechSiteId { get; set; }
    public string? FintechSiteName { get; set; }
    public int? FintechCustomerId { get; set; }
    public string? VendorName { get; set; }
    public bool? IsConfigurationAllowed { get; set; }
    public bool? IsActivationAllowed { get; set; }
    public bool? IsActive { get; set; }
    public string? UserEmail { get; set; }
    public string? Status { get; set; }
    public string? Exception { get; set; }
}
