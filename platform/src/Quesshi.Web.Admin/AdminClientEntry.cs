using Quesshi.Web.Plugins;

namespace Quesshi.Web.Admin;

public sealed class AdminClientEntry : IAdminClientAssemblyEntry
{
    public void Register(IAdminClientRegistry registry)
    {
        registry.RegisterRoot(typeof(AdminApp));
        registry.RegisterPage(typeof(AdminHome));
        registry.RegisterPage(typeof(AdminLogin));
        registry.RegisterPage(typeof(AdminForgot));
        registry.RegisterPage(typeof(AdminReset));
        registry.RegisterPage(typeof(AdminPassword));
        registry.RegisterPage(typeof(AdminAccounts));
    }
}
