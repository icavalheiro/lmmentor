import { Link } from 'react-router-dom';
import { ActionIcon, Badge, Button, Card, Group, Stack, Table, Text, Tooltip } from '@mantine/core';
import { IconChevronRight, IconPlus, IconTrash } from '@tabler/icons-react';
import { useAdminData } from '../context/AdminDataContext';

export function EndpointsPage ()
{
    const { endpoints, models, setEndpoints, setModels } = useAdminData();

    function removeEndpoint ( id: string )
    {
        // Remove o endpoint e os modelos descobertos nele.
        setEndpoints( ( prev ) => prev.filter( ( e ) => e.id !== id ) );
        setModels( ( prev ) => prev.filter( ( m ) => m.endpointId !== id ) );
    }

    return (
        <Card withBorder padding="lg">
            <Stack gap="md">
                <Group justify="space-between">
                    <div>
                        <Text fw={ 600 } size="lg">API Endpoints</Text>
                        <Text c="dimmed" size="sm">
                            Configured endpoints; models are discovered automatically from them.
                        </Text>
                    </div>
                    <Link to="/endpoints/new">
                        <Button leftSection={ <IconPlus size={ 16 } /> }>Add endpoint</Button>
                    </Link>
                </Group>

                <Table striped highlightOnHover withTableBorder>
                    <Table.Thead>
                        <Table.Tr>
                            <Table.Th>Name</Table.Th>
                            <Table.Th>Type</Table.Th>
                            <Table.Th>URL</Table.Th>
                            <Table.Th>Models</Table.Th>
                            <Table.Th>Status</Table.Th>
                            <Table.Th ta="right">Actions</Table.Th>
                        </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                        { endpoints.length === 0 && (
                            <Table.Tr>
                                <Table.Td colSpan={ 6 }>
                                    <Text c="dimmed" ta="center">No endpoints configured.</Text>
                                </Table.Td>
                            </Table.Tr>
                        ) }

                        { endpoints.map( ( endpoint ) =>
                        {
                            const modelCount = models.filter( ( m ) => m.endpointId === endpoint.id ).length;
                            return (
                                <Table.Tr key={ endpoint.id }>
                                    <Table.Td>
                                        <Text fw={ 500 }>{ endpoint.name }</Text>
                                    </Table.Td>
                                    <Table.Td>
                                        <Badge variant="outline" color="gray">{ endpoint.type }</Badge>
                                    </Table.Td>
                                    <Table.Td>
                                        <Text size="sm" c="dimmed">{ endpoint.url }</Text>
                                    </Table.Td>
                                    <Table.Td>
                                        <Text size="sm">{ modelCount } discovered</Text>
                                    </Table.Td>
                                    <Table.Td>
                                        { endpoint.status === 'online' ? (
                                            <Badge color="green" variant="light" size="xs">Online</Badge>
                                        ) : (
                                            <Badge color="red" variant="light" size="xs">Offline</Badge>
                                        ) }
                                    </Table.Td>
                                    <Table.Td ta="right">
                                        <Group gap={ 4 } justify="flex-end">
                                            <Link to={ `/endpoints/${ endpoint.id }` }>
                                                <Tooltip label="View models">
                                                    <ActionIcon variant="subtle" size="sm">
                                                        <IconChevronRight size={ 16 } />
                                                    </ActionIcon>
                                                </Tooltip>
                                            </Link>
                                            <Tooltip label="Remove">
                                                <ActionIcon variant="subtle" color="red" size="sm" onClick={ () => removeEndpoint( endpoint.id ) }>
                                                    <IconTrash size={ 16 } />
                                                </ActionIcon>
                                            </Tooltip>
                                        </Group>
                                    </Table.Td>
                                </Table.Tr>
                            );
                        } ) }
                    </Table.Tbody>
                </Table>
            </Stack>
        </Card>
    );
}
