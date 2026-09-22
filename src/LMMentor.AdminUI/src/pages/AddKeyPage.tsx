import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ActionIcon, Alert, Button, Card, Code, Group, Modal, MultiSelect, Stack, Text, TextInput } from '@mantine/core';
import { IconArrowLeft, IconCopy } from '@tabler/icons-react';
import { useAddKey, useModels } from '../api/queries';
import type { CreatedKey } from '../api/keys';

export function AddKeyPage ()
{
    const navigate = useNavigate();
    const { data: models = [] } = useModels();
    const addKey = useAddKey();

    const [ name, setName ] = useState( '' );
    const [ allowed, setAllowed ] = useState<string[]>( [] );
    const [ createdKey, setCreatedKey ] = useState<CreatedKey | null>( null );
    const [ error, setError ] = useState( '' );

    function handleCreate ()
    {
        if ( !name.trim() || addKey.isPending )
        {
            return;
        }

        setError( '' );
        addKey.mutate(
            { name: name.trim(), allowedModelIds: allowed.length > 0 ? allowed : undefined },
            {
                onSuccess: ( created ) => setCreatedKey( created ),
                onError: ( err ) => setError( err instanceof Error ? err.message : 'Failed to create the key.' ),
            },
        );
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

                { error && <Alert color="red" variant="light">{ error }</Alert> }

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
                    <Button loading={ addKey.isPending } onClick={ handleCreate }>Create key</Button>
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
