using System.Reflection;

namespace JTAuth.Application;

/// <summary>Identifies this assembly for convention-based handler registration.</summary>
public static class AuthApplication
{
    public static Assembly Assembly => typeof(AuthApplication).Assembly;
}
