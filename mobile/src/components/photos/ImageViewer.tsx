import { useState } from 'react';
import type { ReactNode } from 'react';
import { AccessibilityInfo, Animated, Modal, Pressable, StatusBar, StyleSheet, View } from 'react-native';
import { Image } from 'expo-image';
import { SymbolView } from 'expo-symbols';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { GlassButton } from '@/components/profile/GlassButton';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';
import { imageViewerMotion } from '@/theme/motion';

type ImageViewerProps = {
  /** The image to show; the viewer is closed while this is null. */
  uri: string | null;
  imageLabel: string;
  closeLabel: string;
  onClose: () => void;
  /** Extra controls rendered at the right of the top bar. */
  actions?: ReactNode;
  /** Optional line centred under the image. */
  caption?: ReactNode;
  /** Set to let a tap on the image hide every control and show the photo alone on black; this is its accessibility hint. */
  immersiveHint?: string;
};

/** Full-screen image, scaled to fit, with a close button. */
export function ImageViewer({ uri, imageLabel, closeLabel, onClose, actions, caption, immersiveHint }: ImageViewerProps) {
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <Modal visible={uri !== null} animationType="fade" onRequestClose={onClose}>
      <View style={styles.root}>
        {uri ? (
          <ViewerBody
            uri={uri}
            imageLabel={imageLabel}
            closeLabel={closeLabel}
            onClose={onClose}
            actions={actions}
            caption={caption}
            immersiveHint={immersiveHint}
          />
        ) : null}
      </View>
    </Modal>
  );
}

/** The open viewer. It mounts with the image, so the controls are always back when a photo is opened. */
function ViewerBody({
  uri,
  imageLabel,
  closeLabel,
  onClose,
  actions,
  caption,
  immersiveHint,
}: Omit<ImageViewerProps, 'uri'> & { uri: string }) {
  const theme = useTheme();
  const styles = makeStyles(theme);
  const insets = useSafeAreaInsets();
  const [immersive, setImmersive] = useState(false);
  const [chrome] = useState(() => new Animated.Value(1));
  const blackout = chrome.interpolate({ inputRange: [0, 1], outputRange: [1, 0] });

  const toggle = () => {
    const next = !immersive;
    setImmersive(next);
    void AccessibilityInfo.isReduceMotionEnabled().then((reduced) => {
      const target = next ? 0 : 1;
      if (reduced) {
        chrome.setValue(target);
        return;
      }
      Animated.timing(chrome, { toValue: target, duration: imageViewerMotion.fadeDuration, useNativeDriver: true }).start();
    });
  };

  const image = <Image source={{ uri }} style={styles.image} contentFit="contain" accessibilityLabel={imageLabel} />;

  return (
    <>
      {immersiveHint ? <StatusBar hidden={immersive} animated /> : null}
      <Animated.View pointerEvents="none" style={[StyleSheet.absoluteFill, styles.blackout, { opacity: blackout }]} />
      {immersiveHint ? (
        <Pressable
          accessibilityRole="imagebutton"
          accessibilityLabel={imageLabel}
          accessibilityHint={immersiveHint}
          onPress={toggle}
          style={styles.image}
        >
          {image}
        </Pressable>
      ) : (
        image
      )}
      <Animated.View
        pointerEvents={immersive ? 'none' : 'box-none'}
        accessibilityElementsHidden={immersive}
        importantForAccessibility={immersive ? 'no-hide-descendants' : 'auto'}
        style={[StyleSheet.absoluteFill, { opacity: chrome }]}
      >
        <View pointerEvents="box-none" style={[styles.topBar, { top: insets.top + theme.spacing.xs }]}>
          <GlassButton label={closeLabel} onPress={onClose}>
            <SymbolView name="xmark" tintColor={theme.colors.ink} size={theme.sizes.backIcon} />
          </GlassButton>
          {actions}
        </View>
        {caption ? (
          <View pointerEvents="box-none" style={[styles.caption, { bottom: insets.bottom + theme.spacing.xl }]}>
            {caption}
          </View>
        ) : null}
      </Animated.View>
    </>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    root: {
      flex: 1,
      backgroundColor: theme.colors.ground,
    },
    blackout: {
      backgroundColor: theme.glass.viewer.immersive,
    },
    image: {
      flex: 1,
    },
    topBar: {
      position: 'absolute',
      left: theme.spacing.xl,
      right: theme.spacing.xl,
      flexDirection: 'row',
      justifyContent: 'space-between',
    },
    caption: {
      position: 'absolute',
      left: 0,
      right: 0,
      paddingHorizontal: theme.spacing.xl,
      alignItems: 'center',
    },
  });
