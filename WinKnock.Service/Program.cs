using System.Security.Principal;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using WinKnock.Core.Access;
using WinKnock.Core.Configuration;
using WinKnock.Firewall;
using WinKnock.Service;

using (var identity = WindowsIdentity.GetCurrent())
{
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("O WinKnock precisa ser executado como administrador para alterar o firewall.");
        return 1;
    }
}

// Como serviço, o diretório atual é System32; o appsettings.json fica ao lado do .exe
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
});

// Só tem efeito quando o processo roda como serviço; no Visual Studio segue como console
builder.Services.AddWindowsService(o => o.ServiceName = "WinKnock");
builder.Services.Configure<EventLogSettings>(s =>
{
    s.SourceName = "WinKnock";
    s.LogName = "Application";
});

builder.Services
    .AddOptions<WinKnockOptions>()
    .Bind(builder.Configuration.GetSection(WinKnockOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<WinKnockOptions>, WinKnockOptionsValidator>();
builder.Services.AddSingleton<IFirewallController, WindowsFirewallController>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;