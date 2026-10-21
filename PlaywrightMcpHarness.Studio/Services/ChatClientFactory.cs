using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using PlaywrightMcpHarness.Studio.Models;

namespace PlaywrightMcpHarness.Studio.Services;

public interface IChatClientFactory
{
    IChatClient CreateClient(string? providerKey = null, LlmProviderConfig? customOverride = null);
}

public class ChatClientFactory : IChatClientFactory
{
    private static readonly SocketsHttpHandler SharedSocketsHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    };

    private static readonly FinishReasonNormalizingHandler SharedNormalizingHandler = new(SharedSocketsHandler);

    private readonly HarnessConfig _config;

    public ChatClientFactory(IOptions<HarnessConfig> configOptions)
    {
        _config = configOptions.Value;
    }

    public IChatClient CreateClient(string? providerKey = null, LlmProviderConfig? customOverride = null)
    {
        var key = providerKey ?? _config.ActiveProvider;
        LlmProviderConfig provider;

        if (customOverride != null)
        {
            provider = customOverride;
        }
        else if (_config.Providers.TryGetValue(key, out var configuredProvider))
        {
            provider = configuredProvider;
        }
        else
        {
            provider = new LlmProviderConfig
            {
                Type = "Gemini",
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
                ModelId = "gemini-2.5-flash",
                ApiKey = ""
            };
        }

        var timeoutMinutes = _config.LlmTimeoutMinutes > 0 ? _config.LlmTimeoutMinutes : 10;
        var httpClient = new HttpClient(SharedNormalizingHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(timeoutMinutes)
        };

        IChatClient innerClient;

        if (string.Equals(provider.Type, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint) ? "http://localhost:11434" : provider.Endpoint.TrimEnd('/');
            innerClient = new OllamaChatClient(new Uri(endpoint), provider.ModelId, httpClient);
        }
        else if (string.Equals(provider.Type, "Gemini", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(provider.Type, "GoogleGemini", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = provider.ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Gemini API Key is missing. Please enter your Gemini API key in the top navigation bar or the AI Settings modal.");
            }

            var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
                ? "https://generativelanguage.googleapis.com/v1beta/openai/"
                : provider.Endpoint;

            var modelId = string.IsNullOrWhiteSpace(provider.ModelId) ? "gemini-2.5-flash" : provider.ModelId;

            var openAiOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
            };

            var chatClient = new OpenAI.Chat.ChatClient(modelId, new System.ClientModel.ApiKeyCredential(apiKey), openAiOptions);
            innerClient = chatClient.AsIChatClient();
        }
        else if (string.Equals(provider.Type, "OpenWebUI", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = provider.ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = "open-webui";
            }

            var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
                ? "http://localhost:3000/api"
                : provider.Endpoint.TrimEnd('/');

            if (!endpoint.EndsWith("/api", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/api";
            }

            var modelId = string.IsNullOrWhiteSpace(provider.ModelId) ? "llama3:latest" : provider.ModelId;

            var openAiOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
            };

            var chatClient = new OpenAI.Chat.ChatClient(modelId, new System.ClientModel.ApiKeyCredential(apiKey), openAiOptions);
            innerClient = chatClient.AsIChatClient();
        }
        else
        {
            var apiKey = provider.ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = "none";
            }

            var openAiOptions = new OpenAIClientOptions
            {
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
            };

            if (!string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                openAiOptions.Endpoint = new Uri(provider.Endpoint);
            }

            var chatClient = new OpenAI.Chat.ChatClient(provider.ModelId, new System.ClientModel.ApiKeyCredential(apiKey), openAiOptions);
            innerClient = chatClient.AsIChatClient();
        }

        return new ChatClientBuilder(innerClient)
            .UseFunctionInvocation()
            .Build();
    }
}

/// <summary>
/// Normalizes non-standard finish_reason values emitted by OpenAI-compatible endpoints
/// (such as Google Gemini's v1beta/openai/ endpoint) so the OpenAI .NET SDK does not throw
/// ArgumentOutOfRangeException during JSON/SSE deserialization.
/// </summary>
internal sealed class FinishReasonNormalizingHandler : DelegatingHandler
{
    private static readonly Regex NonStandardFinishReasonRegex = new(
        @"""finish_reason""\s*:\s*""(?!stop""|length""|tool_calls""|content_filter""|function_call""|function_call_filter:\s*MALFORMED_FUNCTION_CALL""|MALFORMED_FUNCTION_CALL"")([^""]+)""",
        RegexOptions.Compiled);

    public FinishReasonNormalizingHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content == null)
        {
            return response;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (rawJson.Contains("\"finish_reason\"", StringComparison.Ordinal))
            {
                var sanitized = NormalizeFinishReasons(rawJson);
                if (!ReferenceEquals(rawJson, sanitized))
                {
                    var newContent = new StringContent(sanitized, Encoding.UTF8, "application/json");
                    response.Content = newContent;
                }
            }
        }

        return response;
    }

    internal static string NormalizeFinishReasons(string input)
    {
        return NonStandardFinishReasonRegex.Replace(input, match =>
        {
            var reason = match.Groups[1].Value;
            if (reason.Contains("MAX_TOKENS", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("LENGTH", StringComparison.OrdinalIgnoreCase))
            {
                return "\"finish_reason\":\"length\"";
            }
            if (reason.Contains("SAFETY", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("RECITATION", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("BLOCKLIST", StringComparison.OrdinalIgnoreCase))
            {
                return "\"finish_reason\":\"content_filter\"";
            }
            return "\"finish_reason\":\"stop\"";
        });
    }
}
