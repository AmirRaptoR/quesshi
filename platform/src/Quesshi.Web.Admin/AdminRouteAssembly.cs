using System.Reflection;

namespace Quesshi.Web.Admin;

/// <summary>Provides the web shell with a stable assembly reference for future admin routes.</summary>
public static class AdminRouteAssembly
{
    public static Assembly Assembly => typeof(AdminRouteAssembly).Assembly;
}
