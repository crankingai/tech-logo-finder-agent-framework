using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using OpenAI;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using LogoFinderAgent;


var chatOptions = new ChatOptions
{
   Tools = [AIFunctionFactory.Create(
      BraveSearchService.SearchWeb,
      new AIFunctionFactoryOptions { Description = "Searches the web using Brave Search API" }
   ), // register functions as tools
   AIFunctionFactory.Create(
       ImageValidator.IsValidImageUrl,
       new AIFunctionFactoryOptions { Description = "Validates if the URL resolves to a valid image matching its extension (svg, png, jpg, jpeg)" }
   ),
   AIFunctionFactory.Create(
       ImageSuperResolver.ResolveAsync,
       new AIFunctionFactoryOptions { Description = "Resolves a final direct image URL from a page or URL; extracts og:image/img tags and falls back to Internet Archive if needed" }
   )], // register functions as tools
   
};

var uri = new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException("Please set the AZURE_OPENAI_ENDPOINT environment variable."));
var name = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? throw new InvalidOperationException("Please set the AZURE_OPENAI_DEPLOYMENT_NAME environment variable.");
var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new InvalidOperationException("Please set the AZURE_OPENAI_API_KEY environment variable.");


// Load and process Prompty file
var promptyProcessor = new SimplePromptyProcessor();
var promptyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo-finder.prompty");
var promptyContent = await promptyProcessor.LoadPromptyAsync(promptyPath);

Console.WriteLine($"✓ Loaded Prompty: {promptyContent.Metadata.Name}");
Console.WriteLine($"✓ Description: {promptyContent.Metadata.Description}");

// Get brand name from command line args
var brandName = args.Length > 0 ? args[0] : "Python";
var additionalContext = args.Length > 1 ? args[1] : "";

// Prepare parameters for Prompty template
var promptyParameters = new Dictionary<string, object>
{
    ["brand_name"] = brandName,
    ["additional_context"] = additionalContext,
    ["max_retry_attempts"] = 10
};

// Render system instructions from Prompty
var agentInstructions = promptyProcessor.RenderTemplate(promptyContent.SystemPrompt, promptyParameters);
Console.WriteLine($"✓ System prompt rendered ({agentInstructions.Length} characters)");

// Create agent with Prompty-generated instructions
AIAgent agent = new AzureOpenAIClient(
   uri,
#if false // rely on 'az login' for auth and access
   new AzureCliCredential())
#else // use explicit key - a bit less secure
   new Azure.AzureKeyCredential(key))
#endif
   .GetChatClient(name)
   .CreateAIAgent(instructions: agentInstructions);

// Generate user message from Prompty template
var userMessage = promptyProcessor.RenderTemplate(promptyContent.UserPrompt, promptyParameters);
Console.WriteLine($"📝 User message: {userMessage}");

Console.WriteLine($"🔍 Seeking logo for '{brandName}'...");
Console.WriteLine("━".PadRight(50, '━'));

var result = await agent.RunAsync(userMessage, options: new ChatClientAgentRunOptions(chatOptions));

Console.WriteLine(result);
Console.WriteLine("━".PadRight(50, '━'));
Console.WriteLine($"✅ Logo search completed for '{brandName}'!");
