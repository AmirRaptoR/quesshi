using Microsoft.AspNetCore.Components.Forms;

namespace Quesshi.Web.Admin;

/// <summary>Keeps an import report usable only while its selected inputs remain unchanged.</summary>
public sealed class ImportWorkflow
{
    private int inputVersion;
    private int dryRunVersion = -1;

    public string Kind { get; private set; } = "choice";
    public IBrowserFile? File { get; private set; }
    public IBrowserFile? MediaBundle { get; private set; }
    public ImportReport? Report { get; private set; }
    public bool CanDryRun => File is not null;
    public bool CanImport => dryRunVersion == inputVersion && Report?.Accepted > 0;

    public void SetKind(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Kind = value;
        Invalidate();
    }

    public void SetFile(IBrowserFile? value)
    {
        File = value;
        Invalidate();
    }

    public void SetMediaBundle(IBrowserFile? value)
    {
        MediaBundle = value;
        Invalidate();
    }

    public ImportOperation BeginDryRun()
    {
        if (File is null) throw new InvalidOperationException("A question file is required.");
        Report = null;
        dryRunVersion = -1;
        return new(new ImportInputs(Kind, File, MediaBundle), inputVersion);
    }

    public void CompleteDryRun(ImportOperation operation, ImportReport? report)
    {
        if (operation.InputVersion != inputVersion || !Matches(operation.Inputs)) return;
        Report = report;
        dryRunVersion = report is null ? -1 : inputVersion;
    }

    public bool TryBeginImport(out ImportOperation? operation)
    {
        if (!CanImport || File is null)
        {
            operation = null;
            return false;
        }

        operation = new(new ImportInputs(Kind, File, MediaBundle), inputVersion);
        return true;
    }

    public void CompleteImport(ImportOperation operation, ImportReport? report)
    {
        if (operation.InputVersion != inputVersion || !Matches(operation.Inputs)) return;
        Report = report;
        dryRunVersion = -1;
    }

    private void Invalidate()
    {
        inputVersion++;
        dryRunVersion = -1;
        Report = null;
    }

    private bool Matches(ImportInputs inputs) =>
        string.Equals(Kind, inputs.Kind, StringComparison.Ordinal) &&
        ReferenceEquals(File, inputs.File) &&
        ReferenceEquals(MediaBundle, inputs.MediaBundle);
}

public sealed record ImportOperation(ImportInputs Inputs, int InputVersion);
