import { BarChart, LineChart } from '@mantine/charts';
import { Card, Grid, Stack, Text, Title } from '@mantine/core';
import { mockDailyUsage, mockUsageByKey, mockUsageByModel } from '../data/mockData';

function formatTokens ( value: number )
{
    if ( value >= 1_000_000 )
    {
        return `${ ( value / 1_000_000 ).toLocaleString( 'en-US', { maximumFractionDigits: 1 } ) } M`;
    }
    if ( value >= 1_000 )
    {
        return `${ ( value / 1_000 ).toLocaleString( 'en-US', { maximumFractionDigits: 1 } ) } K`;
    }
    return value.toLocaleString( 'en-US' );
}

function formatDay ( isoDate: string )
{
    return new Date( `${ isoDate }T12:00:00` ).toLocaleDateString( 'en-US', { day: '2-digit', month: '2-digit' } );
}

export function DashboardPage ()
{
    const totalTokens = mockDailyUsage.reduce( ( sum, d ) => sum + d.tokens, 0 );
    const totalRequests = mockDailyUsage.reduce( ( sum, d ) => sum + d.requests, 0 );
    // Média de tokens/s assumindo ~8h de uso ativo por dia na janela analisada.
    const avgTokensPerSecond = Math.round( totalTokens / ( mockDailyUsage.length * 8 * 3600 ) );

    const lineData = mockDailyUsage.map( ( d ) => ( { name: formatDay( d.date ), tokens: d.tokens } ) );
    const modelData = mockUsageByModel.map( ( m ) => ( { name: m.label, tokens: m.tokens } ) );
    const keyData = mockUsageByKey.map( ( k ) => ( { name: k.label, tokens: k.tokens } ) );

    return (
        <Stack gap="lg">
            <div>
                <Title order={ 3 }>Overview</Title>
                <Text c="dimmed" size="sm">API usage summary for the last 7 days.</Text>
            </div>

            <Grid gap="md">
                <Grid.Col span={ { base: 12, sm: 6, lg: 3 } }>
                    <Card withBorder padding="md">
                        <Text c="dimmed" size="sm">Total tokens</Text>
                        <Text fw={ 700 } size="xl">{ formatTokens( totalTokens ) }</Text>
                    </Card>
                </Grid.Col>
                <Grid.Col span={ { base: 12, sm: 6, lg: 3 } }>
                    <Card withBorder padding="md">
                        <Text c="dimmed" size="sm">Requests</Text>
                        <Text fw={ 700 } size="xl">{ totalRequests.toLocaleString( 'en-US' ) }</Text>
                    </Card>
                </Grid.Col>
                <Grid.Col span={ { base: 12, sm: 6, lg: 3 } }>
                    <Card withBorder padding="md">
                        <Text c="dimmed" size="sm">Avg tokens/s</Text>
                        <Text fw={ 700 } size="xl">{ avgTokensPerSecond.toLocaleString( 'en-US' ) }</Text>
                    </Card>
                </Grid.Col>
                <Grid.Col span={ { base: 12, sm: 6, lg: 3 } }>
                    <Card withBorder padding="md">
                        <Text c="dimmed" size="sm">Tokens/day (avg)</Text>
                        <Text fw={ 700 } size="xl">{ formatTokens( Math.round( totalTokens / mockDailyUsage.length ) ) }</Text>
                    </Card>
                </Grid.Col>
            </Grid>

            <Card withBorder padding="lg">
                <Text fw={ 600 } size="lg" mb="md">Token usage per day</Text>
                <LineChart data={ lineData } dataKey="name" series={ [ { name: 'tokens', label: 'Tokens' } ] } h={ 260 } />
            </Card>

            <Grid gap="md">
                <Grid.Col span={ { base: 12, md: 6 } }>
                    <Card withBorder padding="lg">
                        <Text fw={ 600 } size="lg" mb="md">Most used models</Text>
                        <BarChart data={ modelData } dataKey="name" series={ [ { name: 'tokens', label: 'Tokens' } ] } h={ 240 } />
                    </Card>
                </Grid.Col>
                <Grid.Col span={ { base: 12, md: 6 } }>
                    <Card withBorder padding="lg">
                        <Text fw={ 600 } size="lg" mb="md">Most used keys</Text>
                        <BarChart data={ keyData } dataKey="name" series={ [ { name: 'tokens', label: 'Tokens' } ] } h={ 240 } />
                    </Card>
                </Grid.Col>
            </Grid>
        </Stack>
    );
}
