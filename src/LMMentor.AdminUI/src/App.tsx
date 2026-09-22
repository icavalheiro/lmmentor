import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { HashRouter, Route, Routes } from 'react-router-dom';
import { Center, Loader } from '@mantine/core';
import { getCurrentUser } from './api/auth';
import { AdminLayout } from './pages/AdminLayout';
import { AddEndpointPage } from './pages/AddEndpointPage';
import { AddKeyPage } from './pages/AddKeyPage';
import { DashboardPage } from './pages/DashboardPage';
import { EndpointDetailPage } from './pages/EndpointDetailPage';
import { EndpointsPage } from './pages/EndpointsPage';
import { KeysPage } from './pages/KeysPage';
import { LoginPage } from './pages/LoginPage';
import { SettingsPage } from './pages/SettingsPage';

const queryClient = new QueryClient();

function AuthGate ()
{
  const { data, isPending, isError } = useQuery( {
    queryKey: [ 'auth', 'me' ],
    queryFn: getCurrentUser,
    retry: false,
  } );

  if ( isPending )
  {
    return (
      <Center h="100vh">
        <Loader />
      </Center>
    );
  }

  if ( isError || !data )
  {
    return <LoginPage />;
  }

  // HashRouter: as rotas ficam sob /admin/#/... e o backend só precisa servir index.html em /admin.
  return (
    <HashRouter>
      <Routes>
        <Route element={ <AdminLayout /> }>
          <Route index element={ <DashboardPage /> } />
          <Route path="endpoints" element={ <EndpointsPage /> } />
          <Route path="endpoints/new" element={ <AddEndpointPage /> } />
          <Route path="endpoints/:id" element={ <EndpointDetailPage /> } />
          <Route path="keys" element={ <KeysPage /> } />
          <Route path="keys/new" element={ <AddKeyPage /> } />
          <Route path="settings" element={ <SettingsPage /> } />
        </Route>
      </Routes>
    </HashRouter>
  );
}

export default function App ()
{
  return (
    <QueryClientProvider client={ queryClient }>
      <AuthGate />
    </QueryClientProvider>
  );
}
