using Microsoft.AspNetCore.Components.Forms;

namespace Quesshi.Web.Services;

/// <summary>The bulk-import panel's state: which kind and format the picked file is, and the file
/// itself, held until the admin runs a dry run or commits it.</summary>
public sealed class ImportForm
{
    public string Kind { get; set; } = "choice";
    public string Format { get; set; } = "csv";
    public IBrowserFile? File { get; set; }
}
