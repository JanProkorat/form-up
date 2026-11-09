import { useFonts } from 'expo-font';
import { Stack, ThemeProvider } from 'expo-router';
import * as SplashScreen from 'expo-splash-screen';
import { StatusBar } from 'expo-status-bar';
import { PersistQueryClientProvider } from '@tanstack/react-query-persist-client';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { GestureHandlerRootView } from 'react-native-gesture-handler';

import '@/i18n';
import { LaunchScreen } from '@/components/LaunchScreen';
import { ProfileErrorState } from '@/components/ProfileErrorState';
import { useAuthPhase } from '@/hooks/useAuthPhase';
import { newestCachedUpdate, persistOptions } from '@/lib/persistence';
import { queryClient } from '@/lib/queryClient';
import { HomeEffects } from '@/realtime/HomeEffects';
import { RealtimeProvider } from '@/realtime/RealtimeProvider';
import { useAuthStore } from '@/stores/auth';
import { useConnectivityStore } from '@/stores/connectivity';
import { fontAssets, useTheme } from '@/theme';
import { buildNavigationTheme } from '@/theme/navigationTheme';

SplashScreen.preventAutoHideAsync();

/** Screens are reachable only in the phase that owns them; this layout is the sole router. */
function AppStack({ phase, onRetry }: { phase: ReturnType<typeof useAuthPhase>['phase']; onRetry: () => void }) {
  if (phase === 'error') {
    return <ProfileErrorState onRetry={onRetry} />;
  }

  return (
    <Stack screenOptions={{ headerShown: false }}>
      <Stack.Protected guard={phase === 'loggedOut' || phase === 'loading'}>
        <Stack.Screen name="login" />
        <Stack.Screen name="register" />
      </Stack.Protected>
      <Stack.Protected guard={phase === 'unverified'}>
        <Stack.Screen name="check-email" />
      </Stack.Protected>
      {/* Declaration order decides where a phase change lands: verify-email must never be the first screen available in any phase. */}
      <Stack.Protected
        guard={phase === 'loggedOut' || phase === 'loading' || phase === 'unverified'}
      >
        <Stack.Screen name="verify-email" />
      </Stack.Protected>
      <Stack.Protected guard={phase === 'verified'}>
        <Stack.Screen name="verified" />
      </Stack.Protected>
      <Stack.Protected guard={phase === 'onboarding'}>
        <Stack.Screen name="onboarding" />
      </Stack.Protected>
      <Stack.Protected guard={phase === 'coach'}>
        <Stack.Screen name="coach-pending" />
      </Stack.Protected>
      <Stack.Protected guard={phase === 'home'}>
        <Stack.Screen name="(tabs)" />
        <Stack.Screen name="coach-search" />
        <Stack.Screen name="coach/[publicId]" />
        <Stack.Screen name="profile" />
        <Stack.Screen name="invite/[id]" />
        <Stack.Screen name="request/[id]" />
        <Stack.Screen name="chat/[id]" />
        <Stack.Screen name="payments" />
        <Stack.Screen name="nutrition/plan" />
        <Stack.Screen name="nutrition/recipes" />
        <Stack.Screen name="nutrition/food/[foodId]" />
        <Stack.Screen name="nutrition/recipe/[recipeId]" />
        <Stack.Screen name="nutrition/shopping-list" />
        <Stack.Screen name="questionnaire/[linkPublicId]" />
        <Stack.Screen name="photo-request/[id]" />
        <Stack.Screen
          name="notifications"
          options={{ presentation: 'transparentModal', animation: 'fade' }}
        />
      </Stack.Protected>
    </Stack>
  );
}

/** Content plus launch overlay; the overlay lifts once the session and profile are resolved. */
function AppShell() {
  const isInitialized = useAuthStore((state) => state.isInitialized);
  const { phase, retry } = useAuthPhase();
  const [overlayVisible, setOverlayVisible] = useState(true);
  const handleLaunchExited = useCallback(() => setOverlayVisible(false), []);

  return (
    <>
      {isInitialized ? (
        <View
          style={styles.content}
          accessibilityElementsHidden={overlayVisible}
          importantForAccessibility={overlayVisible ? 'no-hide-descendants' : 'auto'}
        >
          <RealtimeProvider enabled={phase === 'home'}>
            <AppStack phase={phase} onRetry={retry} />
            {phase === 'home' ? <HomeEffects /> : null}
          </RealtimeProvider>
        </View>
      ) : null}
      {overlayVisible ? (
        <LaunchScreen
          ready={isInitialized && phase !== 'loading'}
          onExited={handleLaunchExited}
        />
      ) : null}
    </>
  );
}

/** After the snapshot is restored, the banner's "last updated" time comes from the newest restored entry. */
function handleRestored() {
  const newest = newestCachedUpdate();
  if (newest !== null) {
    useConnectivityStore.getState().seedSuccess(newest);
  }
}

export default function RootLayout() {
  const theme = useTheme();
  const [fontsLoaded, fontError] = useFonts(fontAssets);
  const navigationTheme = useMemo(() => buildNavigationTheme(theme), [theme]);
  const fontsReady = fontsLoaded || fontError !== null;

  useEffect(() => {
    void useAuthStore.getState().restoreSession();
  }, []);

  useEffect(() => {
    if (fontsReady) {
      SplashScreen.hideAsync();
    }
  }, [fontsReady]);

  if (!fontsReady) {
    return null;
  }

  return (
    <GestureHandlerRootView style={styles.content}>
      <PersistQueryClientProvider
        client={queryClient}
        persistOptions={persistOptions}
        onSuccess={handleRestored}
      >
        <ThemeProvider value={navigationTheme}>
          <StatusBar style={theme.scheme === 'dark' ? 'light' : 'dark'} />
          <AppShell />
        </ThemeProvider>
      </PersistQueryClientProvider>
    </GestureHandlerRootView>
  );
}

const styles = StyleSheet.create({
  content: {
    flex: 1,
  },
});
