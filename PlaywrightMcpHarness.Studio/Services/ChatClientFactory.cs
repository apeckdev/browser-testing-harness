using System;
using System.Net.Http;
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
        var httpClient = new HttpClient
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
