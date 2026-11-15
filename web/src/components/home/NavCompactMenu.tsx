import { useRef } from 'react';
import { Link } from 'react-router-dom';
import { GlobeIcon, MessageCircleQuestionIcon, MoonIcon, SunIcon, TagIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { scrollToId } from '@/components/home/smoothAnchor';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { useThemeStore } from '@/stores/theme';

const QUESTIONS_SECTION_ID = 'questions';
const LANGUAGES = ['cs', 'en', 'de'] as const;
type Language = (typeof LANGUAGES)[number];

function isLanguage(value: string): value is Language {
  return (LANGUAGES as readonly string[]).includes(value);
}

/** Phone-width replacement for the language switch and theme toggle: one trigger, one menu. */
export default function NavCompactMenu() {
  const { t, i18n } = useTranslation();
  const theme = useThemeStore((s) => s.theme);
  const toggleTheme = useThemeStore((s) => s.toggleTheme);
  const current = isLanguage(i18n.language) ? i18n.language : 'cs';
  const code = t(`entry.languageSwitch.${current}`);
  const toDark = theme === 'light';
  const ThemeIcon = toDark ? MoonIcon : SunIcon;
  const jumpedRef = useRef(false);

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={t('home.nav.menuLabel', { code })}
          className="flex h-10 items-center gap-1.5 rounded-field border border-border bg-surface px-2.5 text-caption font-semibold tracking-caps text-ink hover:bg-sunken"
        >
          <GlobeIcon className="size-4" aria-hidden="true" />
          {code}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        onCloseAutoFocus={(event) => {
          // The menu must not pull focus back to the trigger; the section takes it once the menu has
          // closed (focusing it while the menu is still open does not stick).
          if (jumpedRef.current) {
            event.preventDefault();
            jumpedRef.current = false;
            document.getElementById(QUESTIONS_SECTION_ID)?.focus({ preventScroll: true });
          }
        }}
      >
        <DropdownMenuItem
          onSelect={() => {
            jumpedRef.current = scrollToId(QUESTIONS_SECTION_ID);
          }}
        >
          <MessageCircleQuestionIcon aria-hidden="true" />
          {t('home.nav.questions')}
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <Link to="/pricing">
            <TagIcon aria-hidden="true" />
            {t('home.nav.pricing')}
          </Link>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel>{t('entry.languageSwitch.label')}</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={current}
          onValueChange={(value) => {
            if (isLanguage(value)) {
              void i18n.changeLanguage(value);
            }
          }}
        >
          {LANGUAGES.map((lng) => (
            <DropdownMenuRadioItem key={lng} value={lng}>
              {t(`entry.languageSwitch.${lng}`)}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={toggleTheme}>
          <ThemeIcon aria-hidden="true" />
          {t(toDark ? 'home.nav.themeToDark' : 'home.nav.themeToLight')}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
