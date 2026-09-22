import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import * as endpointsApi from './endpoints';
import * as keysApi from './keys';
import * as settingsApi from './settings';
import { getUsageSummary } from './usage';

// Chaves de query centralizadas para invalidação consistente.
export const queryKeys = {
    endpoints: [ 'endpoints' ] as const,
    models: [ 'models' ] as const,
    keys: [ 'keys' ] as const,
    settings: [ 'settings' ] as const,
    usage: ( days: number ) => [ 'usage', days ] as const,
};

export function useEndpoints ()
{
    return useQuery( { queryKey: queryKeys.endpoints, queryFn: endpointsApi.getEndpoints } );
}

export function useModels ()
{
    return useQuery( { queryKey: queryKeys.models, queryFn: endpointsApi.getAllModels } );
}

export function useKeys ()
{
    return useQuery( { queryKey: queryKeys.keys, queryFn: keysApi.getKeys } );
}

export function useSettings ()
{
    return useQuery( { queryKey: queryKeys.settings, queryFn: settingsApi.getSettings } );
}

export function useUsageSummary ( days = 7 )
{
    return useQuery( { queryKey: queryKeys.usage( days ), queryFn: () => getUsageSummary( days ) } );
}

function useInvalidateAdminData ()
{
    const queryClient = useQueryClient();
    return () =>
        Promise.all( [
            queryClient.invalidateQueries( { queryKey: queryKeys.endpoints } ),
            queryClient.invalidateQueries( { queryKey: queryKeys.models } ),
            queryClient.invalidateQueries( { queryKey: queryKeys.keys } ),
        ] );
}

export function useAddEndpoint ()
{
    const invalidate = useInvalidateAdminData();
    return useMutation( {
        mutationFn: endpointsApi.createEndpoint,
        onSuccess: invalidate,
    } );
}

export function useRemoveEndpoint ()
{
    const invalidate = useInvalidateAdminData();
    return useMutation( {
        mutationFn: ( id: string ) => endpointsApi.deleteEndpoint( id ),
        onSuccess: invalidate,
    } );
}

export function useRefreshEndpoint ()
{
    const invalidate = useInvalidateAdminData();
    return useMutation( {
        mutationFn: ( id: string ) => endpointsApi.refreshEndpoint( id ),
        onSuccess: invalidate,
    } );
}

export function useRenameModel ()
{
    const queryClient = useQueryClient();
    return useMutation( {
        mutationFn: ( { modelId, displayName }: { modelId: string; displayName: string; } ) =>
            endpointsApi.renameModel( modelId, displayName ),
        onSuccess: () => queryClient.invalidateQueries( { queryKey: queryKeys.models } ),
    } );
}

export function useToggleModelEnabled ()
{
    const queryClient = useQueryClient();
    return useMutation( {
        mutationFn: ( { modelId, enabled }: { modelId: string; enabled: boolean; } ) =>
            endpointsApi.setModelEnabled( modelId, enabled ),
        onSuccess: () => queryClient.invalidateQueries( { queryKey: queryKeys.models } ),
    } );
}

export function useAddKey ()
{
    const queryClient = useQueryClient();
    return useMutation( {
        mutationFn: keysApi.createKey,
        onSuccess: () => queryClient.invalidateQueries( { queryKey: queryKeys.keys } ),
    } );
}

export function useRevokeKey ()
{
    const queryClient = useQueryClient();
    return useMutation( {
        mutationFn: ( id: string ) => keysApi.revokeKey( id ),
        onSuccess: () => queryClient.invalidateQueries( { queryKey: queryKeys.keys } ),
    } );
}

export function useDeleteKey ()
{
    const queryClient = useQueryClient();
    return useMutation( {
        mutationFn: ( id: string ) => keysApi.deleteKey( id ),
        onSuccess: () => queryClient.invalidateQueries( { queryKey: queryKeys.keys } ),
    } );
}

export function useUpdateSettings ()
{
    const queryClient = useQueryClient();
    return useMutation( {
        mutationFn: settingsApi.updateSettings,
        onSuccess: () => queryClient.invalidateQueries( { queryKey: queryKeys.settings } ),
    } );
}
