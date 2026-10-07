import { describe, expect, it } from 'vitest';
import type { Model } from '../types';
import { exportSchema, HARNESSES } from './schemaExport';

const models: Model[] = [
    { id: 'internal-id', endpointId: 'endpoint', upstreamModelId: 'upstream/model', displayName: 'public-alias', contextSize: 65536, maxOutputTokens: 8192, enabled: true, blockedWindows: [] },
    { id: 'other-id', endpointId: 'endpoint', upstreamModelId: 'unknown-limits', displayName: '', contextSize: null, maxOutputTokens: null, enabled: true, blockedWindows: [] },
    { id: 'disabled-id', endpointId: 'endpoint', upstreamModelId: 'disabled-model', displayName: '', contextSize: 4096, maxOutputTokens: 1024, enabled: false, blockedWindows: [] },
];
const origin = 'https://lmmentor.example';

describe( 'schema export contracts', () =>
{
    it.each( HARNESSES )( 'exports the public catalog for $label without leaking internal IDs', ( { value } ) =>
    {
        const json = exportSchema( value, models, origin );
        expect( () => JSON.parse( json ) ).not.toThrow();
        expect( json ).toContain( 'public-alias' );
        expect( json ).toContain( 'unknown-limits' );
        expect( json ).not.toMatch( /internal-id|upstream\/model|disabled-model|: null/ );
    } );

    it( 'uses OpenCode limits and environment credentials', () =>
    {
        const provider = JSON.parse( exportSchema( 'opencode', models, origin ) ).provider.lmmentor;
        expect( provider.options ).toEqual( { baseURL: `${ origin }/v1`, apiKey: '{env:LMMENTOR_API_KEY}' } );
        expect( provider.models['public-alias'].limit ).toEqual( { context: 65536, output: 8192 } );
        expect( provider.models['unknown-limits'] ).not.toHaveProperty( 'limit' );
    } );

    it( 'preserves context and output separately for pi and DeepSeek', () =>
    {
        const pi = JSON.parse( exportSchema( 'pi', models, origin ) ).providers.lmmentor;
        const deepseek = JSON.parse( exportSchema( 'deepseek', models, origin ) )[0].config.providers.lmmentor;
        for ( const provider of [ pi, deepseek ] )
        {
            expect( provider.models[0] ).toMatchObject( { id: 'public-alias', contextWindow: 65536, maxTokens: 8192 } );
            expect( provider.models[1] ).not.toHaveProperty( 'contextWindow' );
            expect( provider.models[1] ).not.toHaveProperty( 'maxTokens' );
        }
        expect( pi.baseUrl ).toBe( `${ origin }/v1` );
        expect( deepseek.baseURL ).toBe( `${ origin }/v1` );
    } );

    it( 'uses the VS Code full context window rather than counting output twice', () =>
    {
        const model = JSON.parse( exportSchema( 'vscode', models, origin ) )[0].models[0];
        expect( model ).toMatchObject( { id: 'public-alias', contextWindow: 65536, maxOutputTokens: 8192, url: `${ origin }/v1/chat/completions` } );
        expect( model ).not.toHaveProperty( 'maxInputTokens' );
    } );

    it( 'provides independent Claude settings so session limits are not shared across models', () =>
    {
        const profiles = JSON.parse( exportSchema( 'claude', models, origin ) ).profiles;
        expect( profiles[0].settings ).toEqual( {
            model: 'public-alias',
            env: {
                ANTHROPIC_BASE_URL: origin,
                ANTHROPIC_AUTH_TOKEN: 'REPLACE_WITH_LMMENTOR_API_KEY',
                CLAUDE_CODE_MAX_CONTEXT_TOKENS: '65536',
                CLAUDE_CODE_MAX_OUTPUT_TOKENS: '8192',
            },
        } );
        expect( profiles[1].settings.env ).not.toHaveProperty( 'CLAUDE_CODE_MAX_CONTEXT_TOKENS' );
    } );
} );
