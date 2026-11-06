using PrivateChat.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddHostedService<AgentWorker>();

// TODO: 以 Windows 服务 / 托盘程序运行（v1 以当前用户会话运行，随登录自启）
var host = builder.Build();
host.Run();
