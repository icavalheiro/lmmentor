import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ActionIcon, Button, Card, Code, Group, Modal, MultiSelect, Stack, Text, TextInput } from '@mantine/core';
import { IconArrowLeft, IconCopy } from '@tabler/icons-react';
import { useAdminData } from '../context/AdminDataContext';
import type { ApiKey } from '../types';

function generateMockKey ()
{
    const bytes = new Uint8Array( 24 );
    crypto.getRandomValues( bytes );
    return `sk-lm-${ Array.from( bytes, ( b ) => b.toString( 16 ).padStart( 2, '0' ) ).join( '' ) }`;
}

export function AddKeyPage ()
{
    const navigate = useNavigate();
    const { models, setKeys } = useAdminData();

    const [ name, setName ] = useState( '' );
    const [ allowed, setAllowed ] = useState<string[]>( [] );
    const [ createdKey, setCreatedKey ] = useState<ApiKey | null>( null );

    function handleCreate ()
    {
        if ( !name.trim() )
        {
            return;
        }

        const key: ApiKey = {
            id: `k${ Date.now() }`,
            name: name.trim(),
            key: generateMockKey(),
            allowedModelIds: allowed.length > 0 ? allowed : null,
            createdAt: new Date().toISOString(),
            revokedAt: null,
        };

        setKeys( ( prev ) => [ ...prev, key ] );
        setCreatedKey( key );
    }

    return (
        <Card withBorder padding="lg">
            <Stack gap="md">
                <Group>
                    <Link to="/keys">
                        <Button variant="subtle" color="dimmed" leftSection={ <IconArrowLeft size={ 14 } /> } size="xs">Back</Button>
                    </Link>
                </Group>

                <div>
                    <Text fw={ 600 } size="lg">Add API key</Text>
                    <Text c="dimmed" size="sm">The key will be shown only once, at creation time.</Text>
                </div>

                <TextInput
                    label="Name"
                    placeholder="e.g. IDE Key"
                    value={ name }
                    onChange={ ( e ) => setName( e.currentTarget.value ) }
                    required
                />

                <MultiSelect
                    label="Allowed models (empty = all)"
                    data={ models.map( ( m ) => ( { value: m.id, label: m.displayName || m.upstreamModelId } ) ) }
                    value={ allowed }
                    onChange={ setAllowed }
                />

                <Group justify="flex-end" mt="md">
                    <Link to="/keys">
                        <Button variant="default">Cancel</Button>
                    </Link>
                    <Button onClick={ handleCreate }>Create key</Button>
                </Group>
            </Stack>

            {/* Modal de chave criada (exibe a chave completa uma única vez) */ }
            <Modal opened={ createdKey !== null } onClose={ () => navigate( '/keys' ) } title="Key created" centered>
                <Stack gap="md">
                    <Text size="sm" c="dimmed">
                        Copy this key now. It will not be shown again.
                    </Text>
                    <Group justify="space-between" wrap="nowrap">
                        <Code style={ { flex: 1 } }>{ createdKey?.key }</Code>
                        <ActionIcon
                            variant="light"
                            onClick={ () =>
                            {
                                if ( createdKey )
                                {
                                    navigator.clipboard.writeText( createdKey.key );
                                }
                            } }
                        >
                            <IconCopy size={ 16 } />
                        </ActionIcon>
                    </Group>
                    <Group justify="flex-end">
                        <Button onClick={ () => navigate( '/keys' ) }>Done</Button>
                    </Group>
                </Stack>
            </Modal>
        </Card>
    );
}
