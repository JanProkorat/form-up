import { useTranslation } from 'react-i18next';
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from '@/components/ui/accordion';

const COLUMNS = [
  ['pay', 'invite', 'drafts'],
  ['share', 'reuse', 'languages'],
] as const;

/** "Before you start": six common questions in a two-column accordion, the first one open. */
export default function HomeQuestions() {
  const { t } = useTranslation();

  return (
    <section
      id="questions"
      tabIndex={-1}
      className="flex flex-1 flex-col justify-center px-4 py-14 outline-none sm:px-10 lg:home-section-pad lg:px-16"
    >
      <div className="@container home-tier mx-auto flex w-full max-w-home flex-col items-center gap-10">
        <h2 className="text-center font-display text-home-heading font-semibold tracking-heading text-ink">
          {t('home.questions.title')}
        </h2>
        <div className="grid w-full grid-cols-1 items-start gap-3 lg:grid-cols-2 lg:gap-x-6">
          {COLUMNS.map((column, columnIndex) => (
            <Accordion
              key={column[0]}
              type="multiple"
              defaultValue={columnIndex === 0 ? [column[0]] : []}
              className="flex flex-col gap-3"
            >
              {column.map((item) => (
                <AccordionItem key={item} value={item}>
                  <AccordionTrigger className="text-home-feature-title font-bold text-ink">
                    {t(`home.questions.items.${item}.q`)}
                  </AccordionTrigger>
                  <AccordionContent className="text-home-copy text-muted-foreground">
                    {t(`home.questions.items.${item}.a`)}
                  </AccordionContent>
                </AccordionItem>
              ))}
            </Accordion>
          ))}
        </div>
      </div>
    </section>
  );
}
