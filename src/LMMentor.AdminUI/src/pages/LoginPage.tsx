import { useState } from 'react';

export function LoginPage ()
{
    const [ username, setUsername ] = useState( '' );
    const [ password, setPassword ] = useState( '' );

    // Sem funcionalidade ainda: o submit apenas evita o reload da página.
    function handleSubmit ( event: React.FormEvent<HTMLFormElement> )
    {
        event.preventDefault();
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

                <button type="submit">Entrar</button>
            </form>
        </div>
    );
}
