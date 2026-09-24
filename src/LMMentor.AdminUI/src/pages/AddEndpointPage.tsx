import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Alert, Button, Card, Group, PasswordInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { IconArrowLeft } from '@tabler/icons-react';
import { Link } from 'react-router-dom';
import { useAddEndpoint } from '../api/queries';
import type { EndpointType } from '../types';

const ENDPOINT_TYPES: { value: EndpointType; label: string; }[] = [
    { value: 'openai', label: 'OpenAI' },
    { value: 'deepseek', label: 'DeepSeek' },
    { value: 'ollama', label: 'Ollama' },
    { value: 'groq', label: 'Groq' },
    { value: 'vllm', label: 'vLLM' },
    { value: 'lmstudio', label: 'LM Studio' },
    { value: 'llamacpp', label: 'llama.cpp (llama-server)' },
    { value: 'unsloth', label: 'Unsloth' },
    { value: 'anthropic', label: 'Anthropic (Claude)' },
    { value: 'custom', label: 'Custom (OpenAI-compatible)' },
];

// URL base que o discovery e o relay usam como prefixo ({url}/models e {url}/chat/completions).
// A Anthropic usa a raiz da API: o backend garante o sufixo /v1 nos chamados.
const DEFAULT_URLS: Record<EndpointType, string> = {
    openai: 'https://api.openai.com/v1',
    deepseek: 'https://api.deepseek.com',
    ollama: 'http://localhost:11434',
    groq: 'https://api.groq.com/openai/v1',
    vllm: 'http://localhost:8000/v1',
    lmstudio: 'http://localhost:1234/v1',
    llamacpp: 'http://localhost:8080/v1',
    unsloth: 'http://localhost:8888/v1',
    anthropic: 'https://api.anthropic.com',
    custom: 'https://provider.example.com/v1',
};

export function AddEndpointPage ()
{
    const navigate = useNavigate();
    const addEndpoint = useAddEndpoint();

    const [ name, setName ] = useState( '' );
    const [ type, setType ] = useState<EndpointType>( 'openai' );
    const [ url, setUrl ] = useState( '' );
    const [ token, setToken ] = useState( '' );
    const [ error, setError ] = useState( '' );

    function handleAdd ()
    {
        if ( !name.trim() || !url.trim() || addEndpoint.isPending )
        {
            return;
        }

        setError( '' );
        addEndpoint.mutate(
            { name: name.trim(), type, url: url.trim(), accessToken: token },
            {
                onSuccess: ( created ) => navigate( `/endpoints/${ created.id }` ),
                onError: ( err ) => setError( err instanceof Error ? err.message : 'Failed to add the endpoint.' ),
            },
        );
    }

    return (
        <Card withBorder padding="lg">
            <Stack gap="md">
                <Group>
                    <Link to="/endpoints">
                        <Button variant="subtle" color="dimmed" leftSection={ <IconArrowLeft size={ 14 } /> } size="xs">Back</Button>
                    </Link>
                </Group>

                <div>
                    <Text fw={ 600 } size="lg">Add API endpoint</Text>
                    <Text c="dimmed" size="sm">
                        Models available on this endpoint will be discovered automatically.
                    </Text>
                </div>

                { error && <Alert color="red" variant="light">{ error }</Alert> }

                <TextInput
                    label="Name"
                    placeholder="e.g. Primary OpenAI"
                    value={ name }
                    onChange={ ( e ) => setName( e.currentTarget.value ) }
                    required
                />

                <Select
                    label="Type"
                    data={ ENDPOINT_TYPES }
                    value={ type }
                    onChange={ ( v ) => setType( ( v as EndpointType ) ?? 'openai' ) }
                />

                <TextInput
                    label="Base URL"
                    placeholder={ DEFAULT_URLS[ type ] }
                    value={ url }
                    onChange={ ( e ) => setUrl( e.currentTarget.value ) }
                    required
                />

                <PasswordInput
                    label="Access token (required for cloud providers; optional for local Ollama/LM Studio)"
                    placeholder={ type === 'anthropic' ? 'sk-ant-…' : 'sk-…' }
                    value={ token }
                    onChange={ ( e ) => setToken( e.currentTarget.value ) }
                />

                <Group justify="flex-end" mt="md">
                    <Link to="/endpoints">
                        <Button variant="default">Cancel</Button>
                    </Link>
                    <Button loading={ addEndpoint.isPending } onClick={ handleAdd }>Add and discover models</Button>
                </Group>
            </Stack>
        </Card>
    );
}
