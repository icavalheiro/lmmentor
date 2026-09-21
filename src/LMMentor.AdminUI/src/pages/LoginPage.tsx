import { useState } from 'react';
import
{
    Button,
    Card,
    Container,
    Image,
    PasswordInput,
    Stack,
    Text,
    TextInput,
} from '@mantine/core';
import { login } from '../api/auth';
// Logo compartilhado com o README, servido direto da pasta assets na raiz do repo.
import logoUrl from '../../../../assets/readme-logo.png';

export function LoginPage ()
{
    const [ username, setUsername ] = useState( '' );
    const [ password, setPassword ] = useState( '' );
    const [ error, setError ] = useState( '' );
    const [ submitting, setSubmitting ] = useState( false );

    async function handleSubmit ( event: React.SubmitEvent<HTMLFormElement> )
    {
        event.preventDefault();
        setSubmitting( true );
        setError( '' );

        try
        {
            await login( username, password );
            // Sessão estabelecida via cookie; recarrega para o App exibir o dashboard.
            window.location.href = '/admin/';
        } catch
        {
            setError( 'Invalid username or password.' );
            setSubmitting( false );
        }
    }

    return (
        <Container size="xs" py="xl">
            <Card withBorder shadow="sm" radius="md" padding="lg">
                <Stack gap="md">
                    <div style={ { textAlign: 'center' } }>
                        {/* Logo compartilhado com o README, direto da pasta assets. */ }
                        <Image
                            src={ logoUrl }
                            alt="LMMentor"
                            width="100%"
                            style={ { maxWidth: 275 } }
                            mx="auto"
                        />
                        <Text c="dimmed" size="sm" ta="center" mt={ 4 }>
                            Sign in to manage the aggregator
                        </Text>
                    </div>

                    <form onSubmit={ handleSubmit }>
                        <Stack gap="md">
                            { error && (
                                <Text c="red" size="sm" ta="center">
                                    { error }
                                </Text>
                            ) }

                            <TextInput
                                label="Username"
                                placeholder="admin"
                                value={ username }
                                onChange={ ( e ) => setUsername( e.currentTarget.value ) }
                                autoComplete="username"
                                autoFocus
                                required
                            />

                            <PasswordInput
                                label="Password"
                                placeholder="••••••••"
                                value={ password }
                                onChange={ ( e ) => setPassword( e.currentTarget.value ) }
                                autoComplete="current-password"
                                required
                            />

                            <Button type="submit" fullWidth loading={ submitting }>
                                { submitting ? 'Signing in…' : 'Sign in' }
                            </Button>
                        </Stack>
                    </form>
                </Stack>
            </Card>
        </Container>
    );
}
