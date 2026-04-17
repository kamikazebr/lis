using Lis.Agent.Commands;
using Lis.Tools;
using Lis.Tools.Browser;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Lis.Agent;

public static class AgentSetup {
	public static IServiceCollection AddLisAgent(this IServiceCollection services) {
		services.AddSingleton<Kernel>(sp => {
			IChatClient    chatClient    = sp.GetRequiredService<IChatClient>();
			ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();

			IKernelBuilder builder = Kernel.CreateBuilder();
			builder.Services.AddSingleton<IChatCompletionService>(chatClient.AsChatCompletionService());
			builder.Services.AddSingleton(loggerFactory);

			Kernel kernel = builder.Build();

			// Register plugins using the OUTER service provider (has LisDbContext, embeddings, etc.)
			// builder.Plugins.AddFromType<T>() would resolve from the kernel's internal provider,
			// which shadows IServiceScopeFactory with its own built-in implementation.
			// Short pluginName keeps tool names compact (e.g. "dt_get_current_datetime").
			// We also build an explicit pluginName → CLR Type map so ToolAuthRegistry can read
			// [ToolAuthorization] attributes directly off the plugin classes (SK's KernelFunction
			// wrappers don't expose the underlying MethodInfo reliably across versions — the
			// previous reflection-based approach silently degraded every tool to Open).
			Dictionary<string, Type> pluginTypes = new();
			void Add<T>(string name) where T : class {
				kernel.Plugins.AddFromType<T>(pluginName: name, serviceProvider: sp);
				pluginTypes[name] = typeof(T);
			}

			Add<DateTimePlugin>("dt");
			Add<PromptPlugin>("prompt");
			Add<MemoryPlugin>("mem");
			Add<ConfigPlugin>("cfg");
			Add<ResponsePlugin>("resp");
			Add<ExecPlugin>("exec");
			Add<FileSystemPlugin>("fs");
			Add<WebPlugin>("web");
			Add<BrowserPlugin>("browser");

			// Build auth registry from the explicit plugin type map (deterministic)
			ToolAuthRegistry authRegistry = sp.GetRequiredService<ToolAuthRegistry>();
			authRegistry.Build(pluginTypes);

			ILogger logger = loggerFactory.CreateLogger("ToolAuth");
			if (logger.IsEnabled(LogLevel.Information)) {
				foreach ((string plugin, string func, var level) in authRegistry.Entries()) {
					if (level != Core.Util.ToolAuthLevel.Open)
						logger.LogInformation("Tool auth: {Plugin}.{Func} = {Level}", plugin, func, level);
				}
			}

			return kernel;
		});

		// Tool authorization, policy, and approvals
		services.AddSingleton<ToolAuthRegistry>();
		services.AddSingleton<ToolPolicyService>();
		services.AddSingleton<IApprovalService, ApprovalService>();
		services.AddSingleton<BrowserSessionManager>();

		// Agent
		services.AddSingleton<AgentService>();

		// Commands
		services.AddSingleton<IChatCommand, StatusCommand>();
		services.AddSingleton<IChatCommand, NewSessionCommand>();
		services.AddSingleton<IChatCommand, CompactCommand>();
		services.AddSingleton<IChatCommand, PruneToolsCommand>();
		services.AddSingleton<IChatCommand, ResumeCommand>();
		services.AddSingleton<IChatCommand, AbortCommand>();
		services.AddSingleton<IChatCommand, AgentCommand>();
		services.AddSingleton<IChatCommand, AgentsCommand>();
		services.AddSingleton<IChatCommand, ModelCommand>();
		services.AddSingleton<IChatCommand, ModelsCommand>();
		services.AddSingleton<IChatCommand, ApproveCommand>();
		services.AddSingleton<IChatCommand, DenyCommand>();
		services.AddSingleton<CommandRouter>();

		// Media
		services.AddScoped<IMediaProcessor, MediaProcessor>();

		// Compaction
		services.AddSingleton<CompactionService>();

		return services;
	}
}
