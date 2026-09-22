import { BarChart, LineChart } from '@mantine/charts';
import { Card, Grid, Loader, Stack, Text, Title } from '@mantine/core';
import { useUsageSummary } from '../api/queries';

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
    const { data: summary, isPending } = useUsageSummary( 7 );

    if ( isPending || !summary )
    {
        return (
            <Stack gap="lg" align="center" py="xl">
                <Loader />
            </Stack>
        );
    }

    const lineData = summary.daily.map( ( d ) => ( { name: formatDay( d.date ), tokens: d.tokens } ) );
    const modelData = summary.byModel.map( ( m ) => ( { name: m.label, tokens: m.tokens } ) );
    const keyData = summary.byKey.map( ( k ) => ( { name: k.label, tokens: k.tokens } ) );

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
                        <Text fw={ 700 } size="xl">{ formatTokens( summary.totalTokens ) }</Text>
                    </Card>
                </Grid.Col>
                <Grid.Col span={ { base: 12, sm: 6, lg: 3 } }>
                    <Card withBorder padding="md">
                        <Text c="dimmed" size="sm">Requests</Text>
                        <Text fw={ 700 } size="xl">{ summary.totalRequests.toLocaleString( 'en-US' ) }</Text>
                    </Card>
                </Grid.Col>
                <Grid.Col span={ { base: 12, sm: 6, lg: 3 } }>
                    <Card withBorder padding="md">
                        <Text c="dimmed" size="sm">Avg tokens/s</Text>
                        <Text fw={ 700 } size="xl">{ summary.avgTokensPerSecond.toLocaleString( 'en-US' ) }</Text>
                    </Card>
                </Grid.Col>
                <Grid.Col span={ { base: 12, sm: 6, lg: 3 } }>
                    <Card withBorder padding="md">
                        <Text c="dimmed" size="sm">Tokens/day (avg)</Text>
                        <Text fw={ 700 } size="xl">{ formatTokens( summary.avgTokensPerDay ) }</Text>
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
