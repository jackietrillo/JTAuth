namespace JTAuth.Domain;

/// <summary>How a person proves who they are. Stored by name in the database.</summary>
public enum IdentityProvider
{
    Google,
    Email,
    Phone,
}
