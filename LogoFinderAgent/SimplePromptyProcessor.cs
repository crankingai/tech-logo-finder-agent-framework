using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LogoFinderAgent
{
    /// <summary>
    /// Simple Prompty file parser and processor for basic .prompty file support
    /// </summary>
    public class SimplePromptyProcessor
    {
        private readonly IDeserializer _yamlDeserializer;
        
        public SimplePromptyProcessor()
        {
            _yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
        }

        public async Task<PromptyContent> LoadPromptyAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Prompty file not found: {filePath}");

            var content = await File.ReadAllTextAsync(filePath);
            return ParsePrompty(content);
        }

        public PromptyContent ParsePrompty(string content)
        {
            // Split frontmatter and content
            var parts = content.Split(new[] { "---" }, StringSplitOptions.None);
            
            if (parts.Length < 3)
                throw new InvalidOperationException("Invalid prompty format. Expected YAML frontmatter between --- markers.");

            var yamlContent = parts[1].Trim();
            var promptContent = string.Join("---", parts.Skip(2)).Trim();

            // Parse YAML frontmatter
            var metadata = _yamlDeserializer.Deserialize<PromptyMetadata>(yamlContent) ?? new PromptyMetadata();

            // Split prompt content into system and user sections
            var (systemPrompt, userPrompt) = ParsePromptSections(promptContent);

            return new PromptyContent
            {
                Metadata = metadata,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt
            };
        }

        public string RenderTemplate(string template, Dictionary<string, object> parameters)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            var result = template;

            // Handle simple parameter substitution {{parameter_name}}
            var paramRegex = new Regex(@"\{\{(\w+)\}\}", RegexOptions.IgnoreCase);
            result = paramRegex.Replace(result, match =>
            {
                var paramName = match.Groups[1].Value;
                if (parameters.TryGetValue(paramName, out var value))
                {
                    return value?.ToString() ?? string.Empty;
                }
                return match.Value; // Keep original if parameter not found
            });

            // Handle simple conditionals {% if parameter %} content {% endif %}
            var conditionalRegex = new Regex(@"\{\%\s*if\s+(\w+)\s*\%\}(.*?)\{\%\s*endif\s*\%\}", 
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result = conditionalRegex.Replace(result, match =>
            {
                var paramName = match.Groups[1].Value;
                var conditionalContent = match.Groups[2].Value;
                
                if (parameters.TryGetValue(paramName, out var value))
                {
                    // Check if value is truthy
                    var isTruthy = value switch
                    {
                        null => false,
                        bool b => b,
                        string s => !string.IsNullOrWhiteSpace(s),
                        int i => i != 0,
                        _ => true
                    };
                    
                    return isTruthy ? conditionalContent : string.Empty;
                }
                return string.Empty;
            });

            return result.Trim();
        }

        private (string systemPrompt, string userPrompt) ParsePromptSections(string content)
        {
            var lines = content.Split('\n');
            var systemLines = new List<string>();
            var userLines = new List<string>();
            var currentSection = "system";

            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("system:", StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = "system";
                    continue;
                }
                else if (line.Trim().StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = "user";
                    continue;
                }

                if (currentSection == "system")
                    systemLines.Add(line);
                else if (currentSection == "user")
                    userLines.Add(line);
            }

            return (string.Join('\n', systemLines).Trim(), string.Join('\n', userLines).Trim());
        }
    }

    public class PromptyContent
    {
        public PromptyMetadata Metadata { get; set; } = new();
        public string SystemPrompt { get; set; } = string.Empty;
        public string UserPrompt { get; set; } = string.Empty;
    }

    public class PromptyMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Authors { get; set; } = new();
        public PromptyModel Model { get; set; } = new();
        public Dictionary<string, PromptyParameter> Parameters { get; set; } = new();
    }

    public class PromptyModel
    {
        public string Api { get; set; } = string.Empty;
        public Dictionary<string, object> Configuration { get; set; } = new();
    }

    public class PromptyParameter
    {
        public string Type { get; set; } = string.Empty;
        public object Default { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}