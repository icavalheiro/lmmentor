import { NavLink, Outlet } from 'react-router-dom';
import { AppShell, Button, Group, Stack, Text } from '@mantine/core';
import { IconGauge, IconKey, IconServer, IconSettings } from '@tabler/icons-react';
import { getCurrentUser, logout, type AuthUser } from '../api/auth';
import { useEffect, useState } from 'react';

const NAV_ITEMS = [
    { to: '/', label: 'Dashboard', icon: IconGauge, end: true },
    { to: '/endpoints', label: 'API Endpoints', icon: IconServer, end: false },
    { to: '/keys', label: 'API Keys', icon: IconKey, end: false },
    { to: '/settings', label: 'Settings', icon: IconSettings, end: false },
];

export function AdminLayout ()
{
    const [ user, setUser ] = useState<AuthUser | null>( null );

    useEffect( () =>
    {
        getCurrentUser().then( setUser ).catch( () => setUser( null ) );
    }, [] );

    async function handleLogout ()
    {
        await logout();
        window.location.href = '/admin/';
    }

    return (
        <AppShell header={ { height: 60 } } navbar={ { width: 240, breakpoint: 'sm', collapsed: { mobile: true } } } padding="lg">
            <AppShell.Header px="lg" withBorder>
                <Group h="100%" justify="space-between">
                    <Text fw={ 700 } size="lg">LMMentor</Text>
                    <Group gap="md">
                        { user && <Text size="sm">{ user.username }</Text> }
                        <Button variant="default" size="xs" onClick={ handleLogout }>Sign out</Button>
                    </Group>
                </Group>
            </AppShell.Header>

            <AppShell.Navbar p="sm">
                <Stack gap={ 4 }>
                    { NAV_ITEMS.map( ( item ) => (
                        <NavLink
                            key={ item.to }
                            to={ item.to }
                            end={ item.end }
                            className={ ( { isActive } ) => ( isActive ? 'lm-nav-item lm-nav-item-active' : 'lm-nav-item' ) }
                        >
                            { ( { isActive } ) => (
                                <Group gap="sm" px="md" py="sm">
                                    <item.icon size={ 18 } stroke={ 1.75 } />
                                    <Text size="sm" fw={ isActive ? 600 : 500 }>{ item.label }</Text>
                                </Group>
                            ) }
                        </NavLink>
                    ) ) }
                </Stack>
            </AppShell.Navbar>

            <AppShell.Main>
                <Outlet />
            </AppShell.Main>
        </AppShell>
    );
}
