using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AskFormTests
{
    [Fact]
    public void Additional_prompt_is_optional_in_the_form_and_request()
    {
        var form = new AskForm();
        var oldRequest = new GenerateRequestDto("en", "history", 2, 5);

        Assert.Equal("", form.AdditionalPrompt);
        Assert.Equal("", oldRequest.AdditionalPrompt);
    }
}
