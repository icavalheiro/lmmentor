import { Alert, Card, Center, Loader, Stack, Switch, Text } from '@mantine/core';
import { IconAlertTriangle, IconPlugConnected } from '@tabler/icons-react';
import { useSettings, useUpdateSettings } from '../api/queries';

export function SettingsPage ()
{
    const { data: settings, isPending } = useSettings();
    const updateSettings = useUpdateSettings();

    if ( isPending || !settings )
    {
        return (
            <Center h={ 180 }>
                <Loader />
            </Center>
        );
    }

    const isEnabled = settings.ollamaCompatibilityEnabled;
    const isUpdating = updateSettings.isPending;
    const errorMessage = updateSettings.error instanceof Error ? updateSettings.error.message : null;

    function handleOllamaCompatibilityChange ( checked: boolean )
    {
        updateSettings.mutate( { ollamaCompatibilityEnabled: checked } );
    }

    return (
        <Card withBorder padding="lg">
            <Stack gap="lg">
                <div>
                    <Text fw={ 600 } size="lg">Settings</Text>
                    <Text c="dimmed" size="sm">Configure service-wide LMMentor behavior.</Text>
                </div>

                <Stack gap="xs">
                    <Switch
                        checked={ isEnabled }
                        disabled={ isUpdating }
                        label="Enable Ollama compatibility"
                        description="Exposes the Ollama discovery endpoints used by the VS Code Ollama BYOK provider."
                        onChange={ ( event ) => handleOllamaCompatibilityChange( event.currentTarget.checked ) }
                    />
                </Stack>

                <Alert color={ isEnabled ? 'red' : 'orange' } icon={ <IconAlertTriangle size={ 18 } /> } title="Public relay authentication" variant="light">
                    { isEnabled
                        ? 'Ollama compatibility is enabled. Every request to the public /v1 relay accepts any API key, including no key. Do not enable this for an internet-accessible deployment.'
                        : 'Enabling Ollama compatibility makes every request to the public /v1 relay accept any API key, including no key.' }
                </Alert>

                <Alert color="blue" icon={ <IconPlugConnected size={ 18 } /> } variant="light">
                    Configure the VS Code Ollama provider with this service root URL. Enabled LMMentor models are discovered automatically.
                </Alert>

                { errorMessage && (
                    <Alert color="red" title="Could not save settings">
                        { errorMessage }
                    </Alert>
                ) }
            </Stack>
        </Card>
    );
}