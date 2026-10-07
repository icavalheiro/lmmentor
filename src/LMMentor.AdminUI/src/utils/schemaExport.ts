import type { Model } from '../types';

export const HARNESSES = [
    { value: 'opencode', label: 'OpenCode', destination: 'opencode.json' },
    { value: 'deepseek', label: 'DeepSeek Harness', destination: 'cordis.patch.yml (JSON também é YAML válido)' },
    { value: 'vscode', label: 'VS Code', destination: 'chatLanguageModels.json' },
    { value: 'claude', label: 'Claude', destination: 'Um settings.json por modelo' },
    { value: 'pi', label: 'pi.dev', destination: '~/.pi/agent/models.json' },
] as const;

export type Harness = typeof HARNESSES[number]['value'];

function assumeOutputTokens ( context: number ): number
{
    if ( context > 200000 ) return 131072;
    return Math.min( 32768, Math.floor( context / 2 ) );
}

export function getExportModels ( models: Model[] )
{
    // Exporta o catálogo habilitado, inclusive modelos temporariamente bloqueados por horário.
    return models.filter( ( model ) => model.enabled ).map( ( model ) =>
    {
        const context = model.contextSize && model.contextSize > 0 ? model.contextSize : undefined;
        const output = model.maxOutputTokens && model.maxOutputTokens > 0
            ? model.maxOutputTokens
            : context === undefined ? undefined : assumeOutputTokens( context );
        return {
            id: model.displayName.trim() || model.upstreamModelId,
            context,
            output,
        };
    } );
}

export function exportSchema ( harness: Harness, models: Model[], origin: string ): string
{
    const catalog = getExportModels( models );
    const baseUrl = `${ origin }/v1`;
    let config: unknown;

    switch ( harness )
    {
        case 'opencode':
            config = {
                $schema: 'https://opencode.ai/config.json',
                provider: {
                    lmmentor: {
                        npm: '@ai-sdk/openai-compatible',
                        name: 'LMMentor',
                        options: { baseURL: baseUrl, apiKey: '{env:LMMENTOR_API_KEY}' },
                        models: Object.fromEntries( catalog.map( ( model ) => [ model.id, {
                            id: model.id, name: model.id,
                            ... ( model.context !== undefined || model.output !== undefined
                                ? { limit: { context: model.context, output: model.output } } : {} ),
                        } ] ) ),
                    },
                },
            };
            break;
        case 'deepseek':
            config = [ {
                id: 'llm-pi-ai',
                config: {
                    providers: {
                        lmmentor: {
                            displayName: 'LMMentor', api: 'openai-completions',
                            baseURL: baseUrl, apiKeyEnv: 'LMMENTOR_API_KEY',
                            models: catalog.map( ( model ) => ( {
                                id: model.id, name: model.id,
                                contextWindow: model.context, maxTokens: model.output,
                            } ) ),
                        },
                    },
                },
            } ];
            break;
        case 'vscode':
            config = [ {
                name: 'LMMentor', vendor: 'customendpoint',
                apiKey: '${input:lmmentorApiKey}', apiType: 'chat-completions',
                models: catalog.map( ( model ) => ( {
                    id: model.id, name: model.id, url: `${ baseUrl }/chat/completions`,
                    contextWindow: model.context, maxOutputTokens: model.output,
                } ) ),
            } ];
            break;
        case 'claude':
            // Claude não aceita um catálogo com limites por modelo em settings.json.
            // Cada perfil abaixo é um settings.json independente, selecionado com --settings.
            config = {
                profiles: catalog.map( ( model ) => ( {
                    id: model.id,
                    settings: {
                        model: model.id,
                        env: {
                            ANTHROPIC_BASE_URL: origin,
                            ANTHROPIC_AUTH_TOKEN: 'REPLACE_WITH_LMMENTOR_API_KEY',
                            CLAUDE_CODE_MAX_CONTEXT_TOKENS: model.context?.toString(),
                            CLAUDE_CODE_MAX_OUTPUT_TOKENS: model.output?.toString(),
                        },
                    },
                } ) ),
            };
            break;
        case 'pi':
            config = {
                providers: {
                    lmmentor: {
                        baseUrl, api: 'openai-completions', apiKey: '${LMMENTOR_API_KEY}',
                        models: catalog.map( ( model ) => ( {
                            id: model.id, name: model.id,
                            contextWindow: model.context, maxTokens: model.output,
                        } ) ),
                    },
                },
            };
            break;
    }

    return JSON.stringify( config, null, 2 );
}
