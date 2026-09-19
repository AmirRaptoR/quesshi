using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class CategoryFormTests
{
    [Fact]
    public void Prompt_helper_survives_editing_and_is_trimmed_on_save()
    {
        var category = new CategoryDto("history", "History", "تاریخ", "History", "📜", "#123456",
            true, 2, PromptHelper: "Focus on overlooked events.");

        var form = CategoryForm.From(category);
        form.PromptHelper = "  Prefer specific dates.  ";

        Assert.Equal("Prefer specific dates.", form.ToDto().PromptHelper);
    }
}
