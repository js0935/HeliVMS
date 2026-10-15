using HeliVMS.WebApi;

var app = WebApiHost.Create(args);
app.Run();

/// <summary>Test entry point (M117, section 14.3 P0).</summary>
public partial class Program
{
    /// <summary>Resolves the HeliVms.Web SPA root (config override, else walk up to src/HeliVMS.Web).</summary>
    public static string? FindWebRoot(string? configured) => WebApiHost.FindWebRoot(configured);
}
