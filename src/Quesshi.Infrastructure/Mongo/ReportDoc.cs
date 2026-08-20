namespace Quesshi.Infrastructure.Mongo;

public sealed class ReportDoc
{
    public string PlayerId { get; set; } = "";
    public int Reason { get; set; }
    public DateTime At { get; set; }
}
