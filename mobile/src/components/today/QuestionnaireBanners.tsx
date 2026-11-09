import { answerablePending } from '@/components/photoRequest/model';
import { usePendingQuestionnaires } from '@/hooks/usePendingQuestionnaires';

import { fillableForms } from './pendingForms';
import { PhotoRequestBanner } from './PhotoRequestBanner';
import { QuestionnaireBanner } from './QuestionnaireBanner';

/** A banner per photo request, then per questionnaire, waiting on the client, oldest first; nothing while loading or on failure. */
export function QuestionnaireBanners() {
  const { data } = usePendingQuestionnaires();
  return (
    <>
      {answerablePending(data?.diaryRequests).map((item) => (
        <PhotoRequestBanner key={item.requestPublicId} item={item} />
      ))}
      {fillableForms(data?.questionnaires).map((item) => (
        <QuestionnaireBanner
          key={`${item.linkPublicId}:${item.responsePublicId ?? item.questionnairePublicId ?? ''}`}
          item={item}
        />
      ))}
    </>
  );
}
