public class DocumentNodeResponse
{
    public int? Id { get; set; }
    public string? Tank_Suction_Id { get; set; }
    public int? Tank_Suction_Mapped_Tank { get; set; }
    public int? Number { get; set; }
    public string? Name { get; set; }
    public string? ProductName { get; set; }
    public int? HoseQuantity { get; set; }
    public int? TankCapacity { get; set; }

    public List<DocumentNodeHoseResponse> Hoses { get; set; } = [];
}

public class DocumentNodeHoseResponse
{
    public string? HoseName { get; set; }
    public string? Grade { get; set; }
    public int? RealId { get; set; }
    public string? MappedTankName { get; set; }
}
