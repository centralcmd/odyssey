using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// A minimal <see cref="IHostEnvironment"/> for components that branch on the environment name —
/// currently <c>SmtpEmailSender</c>, whose no-SMTP fallback only logs the action link in
/// Development/Testing (issue #405).
/// </summary>
public sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "Odyssey.Api.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } =
        new PhysicalFileProvider(AppContext.BaseDirectory);
}
