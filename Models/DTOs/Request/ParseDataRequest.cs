using System.Xml.Linq;

public class ParseDataRequest
{
    public XDocument document { get; set; }
    public int Fintech_CustomerId { get; set; }
    public int Fintech_SiteId { get; set; }
    public string Fintech_SiteName { get; set; }
    public int FC_SiteId { get; set; }
    public string? filePath { get; set; }
    public ParseDataNodeRequest Nodes { get; set; }
}

public class ParseDataNodeRequest
{
    public List<DocumentNodeResponse> Tanks { get; set; }
    public List<DocumentNodeResponse> Pumps { get; set; }
    public List<DocumentNodeResponse> Grades { get; set; }
}
