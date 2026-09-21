import { useState } from 'react';
import { login } from '../api/auth';

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
            setError( 'Usuário ou senha inválidos.' );
            setSubmitting( false );
        }
    }

    return (
        <div className="login-page">
            <form className="login-card" onSubmit={ handleSubmit }>
                <h1>LMMentor</h1>
                <p className="login-subtitle">Faça login para gerenciar o agregador</p>

                <label htmlFor="username">Usuário</label>
                <input
                    id="username"
                    type="text"
                    value={ username }
                    onChange={ ( event ) => setUsername( event.target.value ) }
                    autoComplete="username"
                    autoFocus
                />

                <label htmlFor="password">Senha</label>
                <input
                    id="password"
                    type="password"
                    value={ password }
                    onChange={ ( event ) => setPassword( event.target.value ) }
                    autoComplete="current-password"
                />

                { error && <p className="login-error">{ error }</p> }

                <button type="submit" disabled={ submitting }>
                    { submitting ? 'Entrando…' : 'Entrar' }
                </button>
            </form>
        </div>
    );
}
