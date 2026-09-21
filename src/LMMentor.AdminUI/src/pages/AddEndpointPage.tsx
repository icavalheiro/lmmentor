import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, Card, Group, PasswordInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { IconArrowLeft } from '@tabler/icons-react';
import { Link } from 'react-router-dom';
import { useAdminData } from '../context/AdminDataContext';
import type { EndpointType, Model } from '../types';

const ENDPOINT_TYPES: { value: EndpointType; label: string; }[] = [
    { value: 'openai', label: 'OpenAI' },
    { value: 'ollama', label: 'Ollama' },
    { value: 'groq', label: 'Groq' },
    { value: 'vllm', label: 'vLLM' },
    { value: 'lmstudio', label: 'LM Studio' },
    { value: 'unsloth', label: 'Unsloth' },
    { value: 'custom', label: 'Custom (OpenAI-compatible)' },
];

// Modelos fake "descobertos" ao adicionar um endpoint.
const FAKE_DISCOVERED_MODELS: { upstreamModelId: string; contextSize: number | null; }[] = [
    { upstreamModelId: 'llama-3.1-8b-instruct', contextSize: 131072 },
    { upstreamModelId: 'mistral-7b-instruct', contextSize: 32768 },
];

export function AddEndpointPage ()
{
    const navigate = useNavigate();
    const { setEndpoints, setModels } = useAdminData();

    const [ name, setName ] = useState( '' );
    const [ type, setType ] = useState<EndpointType>( 'openai' );
    const [ url, setUrl ] = useState( '' );
    const [ token, setToken ] = useState( '' );

    function handleAdd ()
    {
        if ( !name.trim() || !url.trim() )
        {
            return;
        }

        const endpointId = `e${ Date.now() }`;
        // Simula a descoberta automática de modelos no novo endpoint.
        const discovered: Model[] = FAKE_DISCOVERED_MODELS.map( ( m, i ) => ( {
            id: `${ endpointId }-m${ i }`,
            endpointId,
            upstreamModelId: m.upstreamModelId,
            displayName: '',
            contextSize: m.contextSize,
            enabled: true,
        } ) );

        setEndpoints( ( prev ) => [ ...prev, {
            id: endpointId,
            name: name.trim(),
            type,
            url: url.trim(),
            accessToken: token,
            status: 'online',
            lastCheckedAt: new Date().toISOString(),
        } ] );
        setModels( ( prev ) => [ ...prev, ...discovered ] );

        navigate( `/endpoints/${ endpointId }` );
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
                    placeholder="e.g. https://api.openai.com/v1"
                    value={ url }
                    onChange={ ( e ) => setUrl( e.currentTarget.value ) }
                    required
                />

                <PasswordInput
                    label="Access token (optional for local Ollama/LM Studio)"
                    placeholder="sk-…"
                    value={ token }
                    onChange={ ( e ) => setToken( e.currentTarget.value ) }
                />

                <Group justify="flex-end" mt="md">
                    <Link to="/endpoints">
                        <Button variant="default">Cancel</Button>
                    </Link>
                    <Button onClick={ handleAdd }>Add and discover models</Button>
                </Group>
            </Stack>
        </Card>
    );
}
